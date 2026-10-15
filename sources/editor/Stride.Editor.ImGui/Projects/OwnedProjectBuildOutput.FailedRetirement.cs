namespace Stride.Editor.ImGui;

internal static partial class OwnedProjectBuildOutput
{
    private sealed class FailedOutput(string output)
    {
        internal readonly string Output = output;
        internal readonly string Logs = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(output))!, "failures", Path.GetFileName(output));
        internal readonly object Gate = new();
        internal readonly HashSet<string> Temporary = new(StringComparer.OrdinalIgnoreCase);
        internal bool Preserved, Released;
        internal int Attempts;
        internal string? Error;
        internal void Retire()
        {
            lock (Gate)
            {
                if (Released) return;
                Attempts++;
                try
                {
                    if (!NativeCompilerProcessRetirement.TryDrainForOutput(Output)) return;
                    foreach (var path in Temporary.ToArray())
                    { if (File.Exists(path)) File.Delete(path); Temporary.Remove(path); }
                    if (!Preserved)
                    {
                        if (Directory.Exists(Output))
                        {
                            Directory.CreateDirectory(Logs);
                            foreach (var source in Directory.EnumerateFiles(Output, "*", SearchOption.TopDirectoryOnly)
                                .Where(path => path.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
                                    || path.EndsWith(".asset-diagnostics.jsonl", StringComparison.OrdinalIgnoreCase)))
                            {
                                var target = Path.Combine(Logs, Path.GetFileName(source));
                                var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                                Temporary.Add(temporary);
                                try
                                {
                                    using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                                    using (var archive = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                                    { input.CopyTo(archive); archive.Flush(flushToDisk: true); }
                                    File.Move(temporary, target, overwrite: true);
                                }
                                finally
                                {
                                    if (File.Exists(temporary)) File.Delete(temporary);
                                    Temporary.Remove(temporary);
                                }
                            }
                        }
                        Preserved = true;
                    }
                    Release(Output); Released = true; Error = null;
                }
                catch (Exception error)
                {
                    var text = error.ToString();
                    if (Error != text) EditorBackgroundJobRetirement.ReportFailure("Failed compiler output cleanup retained", error);
                    Error = text;
                }
            }
        }
    }

    private static readonly object failureGate = new();
    private static readonly Dictionary<string, FailedOutput> failedOutputs = new(StringComparer.OrdinalIgnoreCase);
    private static int failureRetryWorker;
    internal static int PendingFailedOutputCount { get { lock (failureGate) return failedOutputs.Count; } }
    internal static string FailedOutputLogDirectory(string output)
    {
        var actual = ValidateRun(output);
        return Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(actual))!, "failures", Path.GetFileName(actual));
    }

    internal static void EnsureFailedOutputAdmission()
    {
        if (PendingFailedOutputCount >= 64)
            throw new IOException("Compiler output cleanup is pending.");
    }

    // Called by the compiler producer only after its real child process and pipe
    // readers settle. A cleanup failure must never replace the original compiler
    // cancellation/failure, and the exact output owner remains retryable.
    internal static void RetireFailedOutput(string output)
    {
        try
        {
            var actual = ValidateRun(output);
            FailedOutput owner;
            lock (failureGate)
            {
                if (!failedOutputs.TryGetValue(actual, out owner!)) failedOutputs.Add(actual, owner = new(actual));
            }
            owner.Retire(); RemoveReleased(owner); StartFailureRetry();
        }
        catch (Exception error) { EditorBackgroundJobRetirement.ReportFailure("Failed compiler output ownership", error); }
    }

    internal static bool TryDrainFailedOutputs()
    {
        FailedOutput[] snapshot;
        lock (failureGate) snapshot = failedOutputs.Values.ToArray();
        foreach (var owner in snapshot) { owner.Retire(); RemoveReleased(owner); }
        return PendingFailedOutputCount == 0;
    }

    internal static object[] CaptureFailedOutputRetirements()
    {
        FailedOutput[] snapshot;
        lock (failureGate) snapshot = failedOutputs.Values.ToArray();
        return snapshot.Select(owner =>
        {
            lock (owner.Gate) return (object)new { output = owner.Output, archivedLogs = owner.Logs,
                logsPreserved = owner.Preserved, released = owner.Released, attempts = owner.Attempts,
                pendingTemporaryFiles = owner.Temporary.ToArray(), error = owner.Error };
        }).ToArray();
    }

    private static void RemoveReleased(FailedOutput owner)
    {
        lock (owner.Gate)
            if (owner.Released)
                lock (failureGate)
                    if (failedOutputs.TryGetValue(owner.Output, out var current) && ReferenceEquals(current, owner))
                        failedOutputs.Remove(owner.Output);
    }

    private static void StartFailureRetry()
    {
        if (PendingFailedOutputCount == 0 || Interlocked.CompareExchange(ref failureRetryWorker, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!TryDrainFailedOutputs()) await Task.Delay(250).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref failureRetryWorker, 0);
                if (PendingFailedOutputCount != 0) StartFailureRetry();
            }
        });
    }
}
