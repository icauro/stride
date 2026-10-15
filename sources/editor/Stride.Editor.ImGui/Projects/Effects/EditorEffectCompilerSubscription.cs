using System.Collections.Concurrent;
using System.Threading.Channels;
using Stride.ConnectionRouter;
using Stride.Engine.Network;
using Stride.Shaders.Compiler;

namespace Stride.Editor.ImGui;

/// <summary>Owns the native router subscriptions; authored assets are touched only by the editor actor.</summary>
internal sealed class EditorEffectCompilerSubscription : IDisposable, IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<byte[]> requests = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256)
    { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly ConcurrentQueue<Exception> failures = new();
    private readonly Task[] connections;
    private readonly SemaphoreSlim routerStartGate = new(1, 1);
    private int disposed;
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task Completion => completion.Task;

    public EditorEffectCompilerSubscription(IEnumerable<string> packageNames)
    {
        var token = lifetime.Token;
        connections = packageNames.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.Ordinal)
            .Select(name => Task.Run(() => Subscribe(name, token))).ToArray();
    }

    private async Task Subscribe(string name, CancellationToken token)
    {
        var delay = 500;
        string? reportedFailure = null;
        while (!token.IsCancellationRequested)
        {
            try
            {
                // Package connections share the native launch boundary. It is never run on the UI actor.
                await routerStartGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    await Task.Run(() =>
                    {
                        token.ThrowIfCancellationRequested();
                        if (!RouterHelper.EnsureRouterLaunched())
                            throw new IOException("The native connection router could not be started.");
                        token.ThrowIfCancellationRequested();
                    }, token).ConfigureAwait(false);
                }
                finally { routerStartGate.Release(); }
                using var socket = await RouterClient.RequestServer(
                    $"/service/Stride.EffectCompilerServer/{StrideVersion.NuGetVersion}/Stride.EffectCompilerServer.exe?mode=gamestudio&packagename={Uri.EscapeDataString(name)}", token).ConfigureAwait(false);
                using var cancellation = token.Register(socket.Dispose);
                token.ThrowIfCancellationRequested();
                var messages = new SocketMessageLayer(socket, false);
                messages.AddPacketHandler<RemoteEffectCompilerEffectRequested>(async packet =>
                {
                    if (!token.IsCancellationRequested && packet.Request is { Length: > 0 })
                    {
                        await requests.Writer.WriteAsync(packet.Request.ToArray(), token).ConfigureAwait(false);
                        Volatile.Write(ref reportedFailure, null);
                        Volatile.Write(ref delay, 500);
                    }
                });
                await messages.MessageLoop().ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                throw new EndOfStreamException("The native effect compiler subscription disconnected.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                if (token.IsCancellationRequested) return;
                var identity = error.GetType().FullName + ":" + error.Message;
                if (Volatile.Read(ref reportedFailure) != identity)
                {
                    Volatile.Write(ref reportedFailure, identity);
                    failures.Enqueue(new IOException($"Effect compiler subscription for '{name}' failed.", error));
                }
            }
            try { await Task.Delay(Volatile.Read(ref delay), token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            Volatile.Write(ref delay, Math.Min(30000, Volatile.Read(ref delay) * 2));
        }
    }

    public bool TryTakeRequest(out byte[] request) => requests.Reader.TryRead(out request!);
    public bool TryTakeFailure(out Exception failure) => failures.TryDequeue(out failure!);
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Exception? cancellationFailure = null;
        try { lifetime.Cancel(); }
        catch (Exception error) { cancellationFailure = error; throw; }
        finally
        {
            requests.Writer.TryComplete();
            while (requests.Reader.TryRead(out _)) { }
            while (failures.TryDequeue(out _)) { }
            _ = Task.WhenAll(connections).ContinueWith(task =>
            {
                var errors = new List<Exception>();
                if (cancellationFailure != null) errors.Add(cancellationFailure);
                if (task.Exception != null) errors.Add(task.Exception);
                try { routerStartGate.Dispose(); }
                catch (Exception error) { errors.Add(error); }
                try { lifetime.Dispose(); }
                catch (Exception error) { errors.Add(error); }
                if (errors.Count == 0) completion.TrySetResult();
                else completion.TrySetException(errors);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    public ValueTask DisposeAsync()
    {
        try { Dispose(); }
        catch { /* Completion retains cancellation and cleanup failures. */ }
        return new ValueTask(completion.Task);
    }
}
