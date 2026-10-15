namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void EnsurePackagePlatformReadersRetired()
    {
        StopPackagePlatformUpdate();
        PumpPackagePlatformUpdate();
        if (IsPackagePlatformUpdating)
            throw new InvalidOperationException("Platform update readers must retire before editor shutdown releases their session and graphics resources.");
    }
    private static void RetireShutdownJob<T>(Task<T> job, CancellationTokenSource? cancellation, Action<T> release, string description)
        => EditorBackgroundJobRetirement.Retire(job, cancellation, release, description);
}
