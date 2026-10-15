using System.Diagnostics;
using Stride.Audio;
using Stride.Core.Diagnostics;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private long nextAudioRecoveryAttempt;
    private string? lastAudioRecoveryError;
    private void PumpEditorAudioDeviceRecovery()
    {
        var engine = Audio?.AudioEngine;
        if (engine == null) return;
        engine.CheckDeviceAvailability();
        if (engine.State == AudioEngineState.Disposed
            || engine.State != AudioEngineState.Invalidated && !engine.DeviceRecoveryCleanupPending) return;
        if (Stopwatch.GetTimestamp() < nextAudioRecoveryAttempt) return;
        try
        {
            if (!engine.TryRecreateDevice()) return;
            nextAudioRecoveryAttempt = 0; lastAudioRecoveryError = null;
            soundPreview?.Pump();
            UpdateEditorAudioFocus(base.IsActiveForTick || platformViewports?.IsEditorFocused == true);
        }
        catch (Exception error)
        {
            nextAudioRecoveryAttempt = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
            if (lastAudioRecoveryError == error.ToString()) return;
            lastAudioRecoveryError = error.ToString();
            outputLog.Append("Audio", LogMessageType.Error, "Device recovery", error.Message, lastAudioRecoveryError);
        }
    }
}
