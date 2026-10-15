namespace Stride.Editor.ImGui;

/// <summary>Retains managed worker ownership through its actual completion after its UI owner closes.</summary>
internal static class EditorBackgroundJobRetirement
{
    internal static void ReportFailure(string description, Exception error) => WriteDiagnostic(description + ": " + Describe(error));
    internal static Task Retire<T>(Task<T> job, CancellationTokenSource? cancellation, Action<T> release, string description)
    {
        try { cancellation?.Cancel(); }
        catch (Exception error) { WriteDiagnostic(description + " cancellation failed: " + Describe(error)); }
        // Callers transfer CPU/session/compiler work only. Graphics and authored
        // actor publication must use their own completion and retirement paths.
        // Result release must preserve its own successful phases across a retry.
        // Scheduling on Default also keeps an already-completed job's cleanup off
        // the closing UI actor; the continuation retains its real result/token.
        return job.ContinueWith(completed => ReleaseCompleted(completed, cancellation, release, description),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
    }

    private static async Task ReleaseCompleted<T>(Task<T> completed, CancellationTokenSource? cancellation,
        Action<T> release, string description)
    {
        if (completed.IsFaulted)
            WriteDiagnostic(description + " ended during shutdown: " + Describe(completed.Exception!));
        var released = completed.Status != TaskStatus.RanToCompletion;
        var tokenDisposed = cancellation == null;
        string? releaseError = null, tokenError = null;
        while (!released || !tokenDisposed)
        {
            if (!released)
            {
                try { release(completed.Result); released = true; }
                catch (Exception error) { ReportCleanupFailure(description, "cleanup", error, ref releaseError); }
            }
            if (released && !tokenDisposed)
            {
                try { cancellation!.Dispose(); tokenDisposed = true; }
                catch (Exception error) { ReportCleanupFailure(description, "token cleanup", error, ref tokenError); }
            }
            if (!released || !tokenDisposed) await Task.Delay(250).ConfigureAwait(false);
        }
    }

    private static void ReportCleanupFailure(string description, string phase, Exception error, ref string? previous)
    {
        var detail = Describe(error);
        if (previous == detail) return;
        previous = detail;
        WriteDiagnostic(description + " " + phase + " failed; retaining ownership for retry: " + detail);
    }

    private static string Describe(Exception error)
    {
        try { return error.ToString(); }
        catch { return error.GetType().FullName ?? "Exception"; }
    }

    private static void WriteDiagnostic(string message)
    {
        try { Console.Error.WriteLine(message); }
        catch { /* A failed diagnostic sink cannot terminate an owned cleanup retry. */ }
    }
}
