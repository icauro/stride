using System.Diagnostics;
using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using ServiceWire.NamedPipes;
using Stride.Core.Diagnostics;
using Stride.Debugger.Target;

namespace Stride.Editor.ImGui;

internal sealed record LiveScriptAssembly(string Project, string Path, Guid ModuleId, byte[] Hash);

/// <summary>Owns the real debugger process, callback pipe and retained remote assembly versions.</summary>
internal sealed class LiveScriptSession : IDisposable
{
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<(string Project, Guid Module), DebugAssembly> versions = [];
    private readonly Dictionary<string, (Guid Module, DebugAssembly Handle)> active = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(string Project, Guid Module), LiveScriptAssembly> snapshots = new();
    private readonly ConcurrentBag<IDisposable> artifactLeases = [];
    private NpHost? pipe;
    private GameDebuggerHost? host;
    private IGameDebuggerTarget? target;
    private Process? process;
    private Stride.AttachedChildProcessJob? processJob;
    private int exited, disposed;
    public bool HasExited => Volatile.Read(ref exited) != 0 || lifetime.IsCancellationRequested || process?.HasExited != false;

    public void RetainArtifact(IDisposable lease) => artifactLeases.Add(lease);
    public LiveScriptAssembly CaptureVersion(string project, string path, Guid module)
    {
        if (snapshots.TryGetValue((Path.GetFullPath(project).ToUpperInvariant(), module), out var retained)) return retained;
        var captured = Capture(project, path);
        if (captured.ModuleId != module) throw new IOException("The source assembly output no longer matches its loaded generation: " + path);
        return captured;
    }

    public static LiveScriptAssembly Capture(string project, string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = SHA256.HashData(file); file.Position = 0;
        using var pe = new PEReader(file); var metadata = pe.GetMetadataReader();
        return new(Path.GetFullPath(project), Path.GetFullPath(path), metadata.GetGuid(metadata.GetModuleDefinition().Mvid), hash);
    }
    public async Task Start(string outputDirectory, LiveScriptAssembly[] assemblies, string[] resolutionPaths,
        LoggerResult logger, CancellationToken cancellation, string graphicsApi = "")
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
        var token = linked.Token; await operations.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (process != null) throw new InvalidOperationException("The live debugger is already started.");
            var directory = Path.Combine(AppContext.BaseDirectory, "tools", "debugger");
            var apphost = Path.Combine(directory, "Stride.Debugger.exe"); var assembly = Path.Combine(directory, "Stride.Debugger.dll");
            var data = Path.Combine(Path.GetFullPath(outputDirectory), "data");
            if (!Directory.Exists(data)) throw new DirectoryNotFoundException(data);
            var address = "Stride/Debugger/ImGui/" + Guid.NewGuid().ToString("N");
            host = new GameDebuggerHost(logger); host.GameExited += () => Interlocked.Exchange(ref exited, 1);
            pipe = new NpHost(address, null, null); pipe.AddService<IGameDebuggerHost>(host); pipe.Open();
            var start = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = outputDirectory,
                RedirectStandardOutput = true, RedirectStandardError = true };
            // Follow the built project's evaluated backend, rather than the editor's preview backend.
            if (!string.IsNullOrWhiteSpace(graphicsApi)) start.Environment["STRIDE_GRAPHICS_API"] = graphicsApi;
            else start.Environment.Remove("STRIDE_GRAPHICS_API");
            if (File.Exists(apphost)) start.FileName = apphost;
            else if (File.Exists(assembly) && File.Exists(Path.ChangeExtension(assembly, ".runtimeconfig.json")))
            { start.FileName = "dotnet"; start.ArgumentList.Add(assembly); }
            else throw new FileNotFoundException("The native Stride debugger is not deployed.", apphost);
            start.ArgumentList.Add("--host=" + address); start.ArgumentList.Add("--data-directory=" + data);
            process = new Process { StartInfo = start, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, args) => { if (args.Data != null) logger.Log(new LogMessage("Live scripting", LogMessageType.Info, args.Data)); };
            process.ErrorDataReceived += (_, args) => { if (args.Data != null) logger.Log(new LogMessage("Live scripting", LogMessageType.Error, args.Data)); };
            process.Exited += (_, _) => Interlocked.Exchange(ref exited, 1);
            if (!process.Start()) throw new InvalidOperationException("The native Stride debugger could not start.");
            processJob = new Stride.AttachedChildProcessJob(process); process.BeginOutputReadLine(); process.BeginErrorReadLine();
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token); handshake.CancelAfter(TimeSpan.FromSeconds(20));
            target = await host.Target.WaitAsync(handshake.Token).ConfigureAwait(false);
            using var cancelProcess = token.Register(KillProcess);
            target.SetAssemblyResolutionPaths(resolutionPaths);
            Apply(assemblies);
            var games = target.GameEnumerateTypeNames();
            var concrete = games.Where(name => name != typeof(Stride.Engine.Game).FullName).Distinct(StringComparer.Ordinal).ToArray();
            var game = concrete.FirstOrDefault() ?? games.SingleOrDefault(name => name == typeof(Stride.Engine.Game).FullName)
                ?? throw new InvalidOperationException("The live project has no game type.");
            target.GameLaunch(game);
            while (!target.GameIsRunning())
            { if (HasExited) throw new InvalidOperationException("The live game exited during startup."); await Task.Delay(20, handshake.Token).ConfigureAwait(false); }
            token.ThrowIfCancellationRequested();
        }
        catch { KillProcess(); throw; }
        finally { operations.Release(); }
    }
    public async Task Update(LiveScriptAssembly[] assemblies, string[] resolutionPaths, CancellationToken cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
        var token = linked.Token; await operations.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            if (HasExited || target == null) throw new InvalidOperationException("The live debugger is no longer connected.");
            using var killOnCancel = token.Register(KillProcess);
            target.SetAssemblyResolutionPaths(resolutionPaths); Apply(assemblies); token.ThrowIfCancellationRequested();
        }
        finally { operations.Release(); }
    }
    private void Apply(LiveScriptAssembly[] assemblies)
    {
        var changes = assemblies.Where(assembly => !active.TryGetValue(assembly.Project, out var before) || before.Module != assembly.ModuleId).ToArray();
        if (changes.Length == 0) return;
        if (changes.Select(assembly => assembly.Project).Distinct(StringComparer.OrdinalIgnoreCase).Count() != changes.Length)
            throw new InvalidOperationException("The live assembly batch contains conflicting project owners.");
        var remove = new List<DebugAssembly>(); var add = new List<DebugAssembly>();
        var prepared = new List<(LiveScriptAssembly Assembly, DebugAssembly Handle)>();
        foreach (var assembly in changes)
        {
            if (active.TryGetValue(assembly.Project, out var before)) remove.Add(before.Handle);
            var key = (assembly.Project.ToUpperInvariant(), assembly.ModuleId);
            if (!versions.TryGetValue(key, out var handle))
            {
                using var file = new FileStream(assembly.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (!SHA256.HashData(file).AsSpan().SequenceEqual(assembly.Hash)) throw new IOException("The live assembly changed before loading: " + assembly.Path);
                file.Position = 0;
                using var bytes = new MemoryStream(); file.CopyTo(bytes);
                var pdbPath = Path.ChangeExtension(assembly.Path, ".pdb");
                byte[]? pdb = null;
                if (File.Exists(pdbPath))
                {
                    using var symbols = new FileStream(pdbPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var symbolBytes = new MemoryStream(); symbols.CopyTo(symbolBytes); pdb = symbolBytes.ToArray();
                }
                // WPF's live compiler sends actual PE/PDB snapshots. A file-path
                // load would hit AssemblyContainer's old same-path cache.
                handle = target!.AssemblyLoadRaw(bytes.ToArray(), pdb);
                if (handle.Equals(DebugAssembly.Empty)) throw new InvalidOperationException("The live debugger could not load " + assembly.Path);
                versions.Add(key, handle);
                snapshots.TryAdd(key, assembly);
            }
            add.Add(handle); prepared.Add((assembly, handle));
        }
        if (!target!.AssemblyUpdate(remove.Distinct().ToList(), add.Distinct().ToList())) throw new InvalidOperationException("The live debugger rejected the actual assembly batch.");
        foreach (var entry in prepared) active[entry.Assembly.Project] = (entry.Assembly.ModuleId, entry.Handle);
    }
    private void KillProcess()
    {
        var child = process;
        try { if (child != null && !child.HasExited) child.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel(); KillProcess();
        _ = Task.Run(async () =>
        {
            await operations.WaitAsync().ConfigureAwait(false);
            try { processJob?.Dispose(); host?.Dispose(); pipe?.Close(); process?.Dispose(); }
            finally
            {
                while (artifactLeases.TryTake(out var lease)) lease.Dispose();
                operations.Release(); lifetime.Dispose();
            }
        });
    }
}
