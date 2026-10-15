using System.Diagnostics;

namespace Stride.Editor.ImGui;

/// <summary>Actual process/readers/log ownership from native start through safe output retirement.</summary>
internal static class NativeCompilerProcessRetirement
{
    internal sealed class Owner
    {
        private readonly object gate = new();
        private readonly Process process;
        private readonly List<Task> readers = [];
        private StreamWriter? log;
        private Microsoft.Win32.SafeHandles.SafeFileHandle? logHandle;
        internal readonly string Output;
        internal readonly int ProcessId;
        private bool retiring, exited, logClosed, released;
        private string? lastError;
        internal Owner(Process process, string output)
        { this.process = process; Output = output; ProcessId = process.Id; }
        internal bool Retiring { get { lock (gate) return retiring; } }
        internal bool Released { get { lock (gate) return released; } }
        internal void RetainLog(StreamWriter value)
        {
            lock (gate)
            {
                log = value;
                if (value.BaseStream is FileStream stream) logHandle = stream.SafeFileHandle;
            }
        }
        internal void RetainReader(Task value) { lock (gate) readers.Add(value); }
        internal void BeginRetirement()
        {
            lock (gate) retiring = true;
            TryRetire(); RemoveReleased(this); StartRetry();
        }
        internal bool TryRetire()
        {
            lock (gate)
            {
                if (released) return true;
                if (!retiring) return false;
                try
                {
                    if (!exited)
                    {
                        if (!process.HasExited)
                        {
                            try { process.Kill(entireProcessTree: true); }
                            catch when (process.HasExited) { }
                        }
                        if (!process.WaitForExit(1000)) return false;
                        exited = true;
                    }
                    if (readers.Any(reader => !reader.IsCompleted)) return false;
                    foreach (var reader in readers)
                        if (reader.IsFaulted) _ = reader.Exception;
                    if (!logClosed)
                    {
                        if (log != null)
                        {
                            try { log.Dispose(); }
                            catch (Exception error)
                            {
                                // Disposal can report a flush error after closing
                                // the actual file. Do not mistake it for an open
                                // native writer, or replace the producer failure.
                                if (logHandle?.IsClosed != true) throw;
                                EditorBackgroundJobRetirement.ReportFailure("Compiler log flush failed after closure", error);
                            }
                        }
                        logClosed = true; log = null; logHandle = null;
                    }
                    process.Dispose(); readers.Clear(); released = true; lastError = null;
                    return true;
                }
                catch (Exception error)
                {
                    var text = error.ToString();
                    if (lastError != text) EditorBackgroundJobRetirement.ReportFailure("Native compiler retirement retained", error);
                    lastError = text; return false;
                }
            }
        }
        internal object Capture()
        { lock (gate) return new { process = ProcessId, output = Output, retiring, exited, readers = readers.Count,
            settledReaders = readers.Count(reader => reader.IsCompleted), logClosed, released, error = lastError }; }
    }

    private static readonly object gate = new();
    private static readonly HashSet<Owner> owners = [];
    private static int retryWorker;
    internal static void EnsureAdmission()
    {
        if (Snapshot().Count(owner => owner.Retiring) >= 64)
            throw new IOException("Native compiler retirement is pending.");
    }
    internal static Owner Register(Process process, string output)
    {
        var owner = new Owner(process, Path.GetFullPath(output));
        lock (gate) owners.Add(owner);
        return owner;
    }
    private static Owner[] Snapshot() { lock (gate) return owners.ToArray(); }
    internal static bool TryDrainForOutput(string output)
    {
        var actual = Path.GetFullPath(output);
        var selected = Snapshot().Where(owner => string.Equals(owner.Output, actual, StringComparison.OrdinalIgnoreCase)).ToArray();
        var ready = true;
        foreach (var owner in selected) { ready &= owner.TryRetire(); RemoveReleased(owner); }
        return ready;
    }
    internal static bool TryDrainRetirements()
    {
        var ready = true;
        foreach (var owner in Snapshot().Where(owner => owner.Retiring))
        { ready &= owner.TryRetire(); RemoveReleased(owner); }
        return ready;
    }
    internal static object[] CaptureRetirements() => Snapshot().Select(owner => owner.Capture()).ToArray();
    private static void RemoveReleased(Owner owner)
    { if (owner.Released) lock (gate) owners.Remove(owner); }
    private static void StartRetry()
    {
        if (!Snapshot().Any(owner => owner.Retiring) || Interlocked.CompareExchange(ref retryWorker, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try { while (!TryDrainRetirements()) await Task.Delay(250).ConfigureAwait(false); }
            finally
            {
                Interlocked.Exchange(ref retryWorker, 0);
                if (Snapshot().Any(owner => owner.Retiring)) StartRetry();
            }
        });
    }
}
