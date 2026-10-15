using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Stride.Editor.Thumbnails;

namespace Stride.Editor.ImGui;

internal sealed class NativeThumbnailWorkerDeviceException(string message) : Exception(message);
internal class NativeThumbnailWorkerUnavailableException(string message, Exception? failure = null) : Exception(message, failure);
internal sealed class NativeThumbnailWorkerInitializationException(Exception failure, IAsyncDisposable cleanup)
    : NativeThumbnailWorkerUnavailableException("The thumbnail worker failed to initialize and its actual cleanup remains pending.", failure)
{
    internal IAsyncDisposable Cleanup { get; } = cleanup;
}

// Generation-owned real child client, reused across compatible visible bursts.
internal sealed class NativeThumbnailResidentWorker : IAsyncDisposable
{
    private static string Root => Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Stride", "Editor.ImGui", "native-thumbnails", "resident"));
    private readonly string directory, deployment;
    internal string Generation { get; }
    internal int ProcessId { get; }
    private readonly Process process;
    private readonly Stride.AttachedChildProcessJob job;
    private readonly Action<ProjectLoadState> progress;
    private readonly SemaphoreSlim write = new(1, 1), operation = new(1, 1);
    private readonly ConcurrentDictionary<string, Pending> pending = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task stdout, stderr, exit;
    private Exception? terminalFailure;
    private int closing;
    private sealed class Pending(NativeThumbnailBatchPlan plan)
    {
        internal readonly NativeThumbnailBatchPlan Plan = plan;
        internal readonly List<NativeThumbnailBatchResult> Results = [];
        internal readonly TaskCompletionSource<IReadOnlyList<NativeThumbnailBatchResult>> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private NativeThumbnailResidentWorker(string directory, string deployment, NativeThumbnailBatchPlan plan,
        Process process, Action<ProjectLoadState> progress)
    {
        this.directory = directory; this.deployment = deployment; this.process = process; this.progress = progress;
        Generation = plan.Generation; ProcessId = process.Id; job = new Stride.AttachedChildProcessJob(process);
        stdout = Read(process.StandardOutput, true); stderr = Read(process.StandardError, false);
        exit = ObserveExit();
    }

    internal static async Task<NativeThumbnailResidentWorker> StartAsync(NativeThumbnailBatchPlan plan,
        Action<ProjectLoadState> progress, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var directory = Path.Combine(Root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        NativeThumbnailResidentWorker? worker = null;
        Process? launched = null;
        try
        {
            var deployment = Path.Combine(directory, "worker");
            var executable = NativeThumbnailWorkerDeployment.Create(deployment, plan.Requests[0], cancellation);
            var initial = Bind(plan.Stage(directory, Path.Combine(Root, "cache", plan.Generation, "objects"), cancellation), deployment);
            var initialization = Path.Combine(directory, "initialization.json");
            File.WriteAllText(initialization, JsonSerializer.Serialize(initial));
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
                WorkingDirectory = directory, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["STRIDE_GRAPHICS_API"] = EditorGraphicsBackend.SelectedApi;
            start.Environment["STRIDE_EDITOR_ASSET_DIAGNOSTICS"] = Path.Combine(directory, "diagnostics.jsonl");
            start.ArgumentList.Add("preview-thumbnail-session"); start.ArgumentList.Add(initialization);
            var process = launched = Process.Start(start) ?? throw new InvalidOperationException("Could not start the native thumbnail worker.");
            worker = new(directory, deployment, plan, process, progress);
            await worker.ready.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellation).ConfigureAwait(false);
            return worker;
        }
        catch (Exception failure)
        {
            IAsyncDisposable cleanup = worker ?? (IAsyncDisposable)new FailedStartup(directory, launched);
            try { await cleanup.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanupFailure)
            { throw new NativeThumbnailWorkerInitializationException(new AggregateException(failure, cleanupFailure), cleanup); }
            throw;
        }
    }
    private sealed class FailedStartup(string directory, Process? process) : IAsyncDisposable
    {
        private bool exited, processReleased, directoryReleased;
        public async ValueTask DisposeAsync()
        {
            if (process != null && !exited)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync().ConfigureAwait(false);
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false); exited = true;
            }
            if (process != null && !processReleased) { process.Dispose(); processReleased = true; }
            if (!directoryReleased) { RemoveOwned(directory); directoryReleased = true; }
        }
    }

    internal async Task<IReadOnlyList<NativeThumbnailBatchResult>> BuildAsync(NativeThumbnailBatchPlan plan,
        IReadOnlyDictionary<string, CancellationToken> rootCancellation, CancellationToken cancellation)
    {
        if (plan.Generation != Generation) throw new InvalidDataException("A resident worker cannot accept a different certified generation.");
        await operation.WaitAsync(cancellation).ConfigureAwait(false);
        var requestId = Guid.NewGuid().ToString("N"); var folder = Path.Combine(directory, "batches", requestId);
        var state = new Pending(plan); var registrations = new List<CancellationTokenRegistration>();
        try
        {
            if (Volatile.Read(ref closing) != 0) throw new ObjectDisposedException(nameof(NativeThumbnailResidentWorker));
            var request = Bind(plan.Stage(folder, Path.Combine(Root, "cache", Generation, "objects"), cancellation), deployment);
            var path = Path.Combine(folder, "request.json"); File.WriteAllText(path, JsonSerializer.Serialize(request));
            if (!pending.TryAdd(requestId, state)) throw new InvalidOperationException("Duplicate native batch request identity.");
            await Send("run", requestId, path).ConfigureAwait(false);
            foreach (var root in rootCancellation)
                registrations.Add(root.Value.Register(() => _ = Cancel("cancel-root", requestId, root.Key)));
            registrations.Add(cancellation.Register(() => _ = Cancel("cancel-batch", requestId, "")));
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            registrations.Add(deadline.Token.Register(() => _ = Cancel("cancel-batch", requestId, "")));
            // Cancellation publication waits for the real worker's completion
            // record. Assets sharing the same build cannot dispose its inputs early.
            var results = await state.Completion.Task.ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            return results;
        }
        finally
        {
            foreach (var registration in registrations) registration.Dispose();
            pending.TryRemove(requestId, out _);
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            operation.Release();
        }
    }

    private async Task Cancel(string kind, string requestId, string identity)
    {
        try
        {
            await Send(kind, requestId, "", identity).ConfigureAwait(false);
            if (kind == "cancel-batch")
            {
                if (pending.TryGetValue(requestId, out var request))
                {
                    await Task.WhenAny(request.Completion.Task, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
                    if (!request.Completion.Task.IsCompleted)
                        Fail(new TimeoutException("The cancelled native thumbnail batch did not drain within its shutdown bound."));
                }
            }
        }
        catch (Exception error) { Fail(error); }
    }
    private async Task Send(string operation, string requestId, string path = "", string identity = "")
    {
        await write.WaitAsync().ConfigureAwait(false);
        try
        {
            if (process.HasExited) throw new InvalidOperationException("The native thumbnail worker has exited.");
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { Version = 1,
                Operation = operation, RequestId = requestId, RequestPath = path, RootIdentity = identity })).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        finally { write.Release(); }
    }
    private async Task Read(StreamReader stream, bool protocol)
    {
        try
        {
            while (await stream.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                const string prefix = "@@stride-thumbnail:";
                if (!protocol || !line.StartsWith(prefix, StringComparison.Ordinal))
                { progress(new ProjectLoadState("Thumbnail", line.Length > 300 ? line[..300] + "…" : line, directory, process.Id, line)); continue; }
                if (line.Length > 2 * 1024 * 1024) throw new InvalidDataException("The resident thumbnail output exceeds its protocol bound.");
                using var json = JsonDocument.Parse(line[prefix.Length..]); var envelope = json.RootElement;
                if (envelope.GetProperty("version").GetInt32() != 1) throw new InvalidDataException("Unsupported resident thumbnail response version.");
                var kind = envelope.GetProperty("kind").GetString(); var requestId = envelope.GetProperty("requestId").GetString()!;
                if (kind == "ready")
                {
                    if (envelope.GetProperty("value").GetString() != Generation) throw new InvalidDataException("The resident thumbnail worker initialized the wrong generation.");
                    ready.TrySetResult(); continue;
                }
                if (!pending.TryGetValue(requestId, out var state)) throw new InvalidDataException("The resident worker returned an unknown request identity.");
                if (kind == "result")
                {
                    var result = JsonSerializer.Deserialize<NativeThumbnailBatchResult>(envelope.GetProperty("value").GetRawText())!;
                    if (!state.Plan.Requests.Any(root => root.Identity == result.Identity && root.Authored.Parameters.RootId == result.AssetId)
                        || state.Results.Any(root => root.Identity == result.Identity) || result.Png?.Length > 1024 * 1024
                        || !Enum.IsDefined(result.Result) || !Enum.IsDefined(result.Level)
                        || (result.Result == ThumbnailBuildResult.Succeeded && result.Png is not { Length: > 0 })
                        || (result.Result != ThumbnailBuildResult.Succeeded && result.Png != null))
                        throw new InvalidDataException("The resident worker returned mismatched root provenance.");
                    state.Results.Add(result);
                }
                else if (kind == "complete")
                {
                    if (envelope.GetProperty("value").GetString() != Generation || state.Results.Count != state.Plan.Requests.Length)
                        throw new InvalidDataException("The resident worker did not complete the exact requested batch.");
                    state.Completion.TrySetResult(state.Results.ToArray());
                }
                else if (kind == "cancelled") state.Completion.TrySetCanceled();
                else if (kind == "generation-failure") Fail(new NativeThumbnailWorkerDeviceException(envelope.GetProperty("value").GetString()!));
                else if (kind == "failure") state.Completion.TrySetException(new InvalidOperationException(envelope.GetProperty("value").GetString()));
                else throw new InvalidDataException("Unknown resident thumbnail response.");
            }
        }
        catch (Exception error) { Fail(error); }
    }
    private async Task ObserveExit()
    {
        await process.WaitForExitAsync().ConfigureAwait(false);
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        var error = terminalFailure ?? new InvalidOperationException("The resident thumbnail worker exited with code " + process.ExitCode);
        ready.TrySetException(error);
        foreach (var request in pending.Values) request.Completion.TrySetException(error);
    }
    private void Fail(Exception error)
    {
        Interlocked.CompareExchange(ref terminalFailure, error, null);
        // Failed protocol/provenance is terminal for this generation. Kill/drain
        // before a caller can remove its staged inputs or deploy a replacement.
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }
    private readonly object disposalGate = new();
    private Task? disposal;
    private bool inputClosed, exitDrained, jobReleased, processReleased, directoryReleased;
    public ValueTask DisposeAsync()
    {
        lock (disposalGate)
        {
            Interlocked.Exchange(ref closing, 1);
            if (disposal == null || disposal.IsFaulted || disposal.IsCanceled) disposal = Task.Run(DisposeCoreAsync);
            return new ValueTask(disposal);
        }
    }
    private async Task DisposeCoreAsync()
    {
        if (!inputClosed)
        {
            try { await Send("stop", "").ConfigureAwait(false); } catch (IOException) { } catch (InvalidOperationException) { }
            process.StandardInput.Close(); inputClosed = true;
        }
        if (!exitDrained)
        {
            try { await exit.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await exit.ConfigureAwait(false);
            }
            exitDrained = true;
        }
        await operation.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!jobReleased) { job.Dispose(); jobReleased = true; }
            if (!processReleased) { process.Dispose(); processReleased = true; }
            if (!directoryReleased) { RemoveOwned(directory); directoryReleased = true; }
        }
        finally { operation.Release(); }
    }
    private static NativeThumbnailBatchParameters Bind(NativeThumbnailBatchParameters request, string deployment)
    {
        request.Assemblies = request.Assemblies.Select(item => new NativeThumbnailAssemblyInput {
            Identity = item.Identity, ModuleVersionId = item.ModuleVersionId, Sha256 = item.Sha256,
            Path = Path.Combine(deployment, new AssemblyName(item.Identity).Name + ".dll") }).ToArray();
        request.Inputs.AssemblyPaths = request.Assemblies.Select(item => item.Path).ToArray(); return request;
    }
    private static void RemoveOwned(string directory)
    {
        var absolute = Path.GetFullPath(directory);
        if (!string.Equals(Path.GetDirectoryName(absolute), Root, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(absolute), "N", out _))
            throw new InvalidOperationException("The worker directory is outside its owned generation root.");
        if (Directory.Exists(absolute)) Directory.Delete(absolute, true);
    }
}
