namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    internal IReadOnlyList<string> VerifyShellParityBatch(string artifactDirectory)
        => ShellParityChecks.Run(artifactDirectory);
    private ProjectSaveParityRun? shellParitySaveCleanup;
    private readonly List<(Task Task, CancellationTokenSource? Cancellation)> shellParityAsyncCleanup = [];
    internal void QueueShellParityAsyncCleanup(Task task, CancellationTokenSource? cancellation = null)
    {
        if (shellParityAsyncCleanup.All(item => !ReferenceEquals(item.Task, task)))
            shellParityAsyncCleanup.Add((task, cancellation));
    }
    internal void QueueShellParitySaveCleanup(ProjectSaveParityRun run)
    {
        if (shellParitySaveCleanup != null && !ReferenceEquals(shellParitySaveCleanup, run))
            throw new InvalidOperationException("A different parity save fixture is still retiring.");
        shellParitySaveCleanup = run;
        DrainShellParityCleanup();
    }
    // The parent terminal pump invokes this once per frame, after its normal
    // package metadata save pump. False means a real reader/lease still owns work.
    internal bool DrainShellParityCleanup()
    {
        if (shellParityAsyncCleanup.Count != 0)
        {
            if (shellParityAsyncCleanup.Any(item => !item.Task.IsCompleted)) return false;
            if (!TryDrainNativeThumbnailIdleVerification()) return false;
            if (!TryDrainP02DependencyRestoreParity()) return false;
            if (!TryDrainP02NugetLibraryParity()) return false;
            // The probe owns the operation's pass/fail result. The retained
            // owners above independently certify physical cleanup, including
            // retry after a fault; an operation failure must still close safely.
            try { foreach (var item in shellParityAsyncCleanup) if (item.Task.IsFaulted) _ = item.Task.Exception; }
            finally { foreach (var item in shellParityAsyncCleanup) item.Cancellation?.Dispose(); }
            shellParityAsyncCleanup.Clear();
        }
        if (shellParitySaveCleanup == null) return true;
        if (!RetireProjectSaveParityVerification(shellParitySaveCleanup)) return false;
        shellParitySaveCleanup = null; return true;
    }
    internal bool TryDrainShellParityCleanup() => DrainShellParityCleanup();
}
