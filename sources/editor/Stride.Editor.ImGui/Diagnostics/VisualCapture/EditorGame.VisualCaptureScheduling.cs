namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    // Read-only scheduling inputs. Do not evaluate IsActiveForTick: that getter
    // also observes audio focus and is an ownership-actor behavior, not telemetry.
    private object InspectVisualCaptureScheduling() => new
    {
        rawGameIsActive = IsActive,
        nativeWindowFocused = Window?.Focused,
        nativeEditorWindowFocused = platformViewports?.IsEditorFocused,
        nativeWindowVisible = Window?.Visible,
        nativeWindowMinimized = Window?.IsMinimized,
        IsRunning, IsExiting, IsFixedTimeStep,
        TreatNotFocusedLikeMinimized, DrawWhileMinimized,
        windowMinimumUpdateMilliseconds = WindowMinimumUpdateRate.MinimumElapsedTime.TotalMilliseconds,
        windowThrottlerType = WindowMinimumUpdateRate.Type.ToString(),
        minimizedMinimumUpdateMilliseconds = MinimizedMinimumUpdateRate.MinimumElapsedTime.TotalMilliseconds,
        minimizedThrottlerType = MinimizedMinimumUpdateRate.Type.ToString(),
        synchronizeWithVerticalRetrace = GraphicsDeviceManager.SynchronizeWithVerticalRetrace,
        nativeUpdateFrame = UpdateTime.FrameCount,
        nativeDrawFrame = DrawTime.FrameCount,
        nativeUpdateTotalSeconds = UpdateTime.Total.TotalSeconds,
        nativeDrawTotalSeconds = DrawTime.Total.TotalSeconds,
        backgroundLaunch = WindowsBackgroundLaunch.Enabled,
        importPollingInputCaptureCount,
        importPollingEligibleOwnerCount,
        importPollingNextCheckUtc = importSettingsCheckAt
    };
}
