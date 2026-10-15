using Stride.Audio;
using Stride.Core;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private AudioSystem Audio => base.Services.GetSafeServiceAs<AudioSystem>();
    private AudioEngine? focusSuspendedAudioEngine;
    private bool previousAudioActivationPolicy, editorAudioFocusInitialized;
    private void InitializeEditorAudioFocus()
    {
        if (editorAudioFocusInitialized) return;
        previousAudioActivationPolicy = Audio.HandleGameActivation;
        Audio.HandleGameActivation = false; editorAudioFocusInitialized = true;
    }
    private void PumpEditorAudioFocus()
    {
        if (!editorAudioFocusInitialized) InitializeEditorAudioFocus();
        PumpEditorAudioDeviceRecovery();
        UpdateEditorAudioFocus(base.IsActiveForTick || platformViewports?.IsEditorFocused == true);
    }
    private bool ObserveEditorAudioFocus(bool focused)
    {
        // GameBase asks this before deciding whether Update runs. Observing here
        // also suspends audio when every editor HWND is inactive.
        if (editorAudioFocusInitialized) { PumpEditorAudioDeviceRecovery(); UpdateEditorAudioFocus(focused); }
        return focused;
    }
    private void UpdateEditorAudioFocus(bool focused)
    {
        var engine = Audio.AudioEngine;
        if (engine == null || engine.State is AudioEngineState.Invalidated or AudioEngineState.Disposed)
        { if (!ReferenceEquals(engine, focusSuspendedAudioEngine) || engine?.State == AudioEngineState.Disposed) focusSuspendedAudioEngine = null; return; }
        if (focusSuspendedAudioEngine != null && !ReferenceEquals(engine, focusSuspendedAudioEngine)) focusSuspendedAudioEngine = null;
        if (focused)
        {
            if (!ReferenceEquals(engine, focusSuspendedAudioEngine)) return;
            if (engine.State == AudioEngineState.Paused) engine.ResumeAudio();
            focusSuspendedAudioEngine = null;
        }
        else if (engine.State == AudioEngineState.Running)
        {
            soundPreview?.Pump();
            engine.PauseAudio(); engine.Update();
            if (engine.State == AudioEngineState.Paused) focusSuspendedAudioEngine = engine;
        }
    }
    private void RestoreEditorAudioFocus()
    {
        if (!editorAudioFocusInitialized) return;
        var suspended = focusSuspendedAudioEngine; focusSuspendedAudioEngine = null;
        if (suspended != null && ReferenceEquals(suspended, Audio.AudioEngine) && suspended.State == AudioEngineState.Paused)
            suspended.ResumeAudio();
        Audio.HandleGameActivation = previousAudioActivationPolicy; editorAudioFocusInitialized = false;
    }
}
