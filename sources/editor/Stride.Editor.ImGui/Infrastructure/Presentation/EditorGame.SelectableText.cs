using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private readonly Dictionary<uint, EditorSelectableText> selectableText = [];
    private void DrawSelectableText(string id, string text, bool wrap = true, System.Numerics.Vector2? size = null, bool nativeShaping = true, bool followTail = false, string nativeFontFamily = "Segoe UI")
    {
        var key = Gui.GetID(id);
        if (!selectableText.TryGetValue(key, out var view)) selectableText.Add(key, view = new());
        var suspended = IsEditorGpuRetirementPending || IsEditorGpuRetirementFinalizing || IsEditorGraphicsRecovering;
        view.NativeFontFamily = nativeFontFamily;
        view.UseNativeShaping = nativeShaping && !suspended;
        if (nativeShaping && suspended) view.SuspendNativePresentation();
        if (view.UseNativeShaping)
        {
            view.NativeGlyphImages = GetScriptGlyphImages(); view.NativeGlyphCommands = GraphicsContext.CommandList;
        }
        view.Draw(id, text, wrap, size, followTail: followTail);
    }
    private void ClearSelectableTextViews()
    {
        foreach (var view in selectableText.Values) view.Dispose();
        selectableText.Clear();
        DisposeNativeWidgetText();
    }
    private void SuspendNativeReadOnlyTextPresentation()
    {
        foreach (var view in selectableText.Values) view.SuspendNativePresentation();
        aboutBackersTextView?.Suspend();
        outputPanelState?.SuspendNativeText();
    }
    private void DisposeNativeReadOnlyTextViews()
    {
        ClearSelectableTextViews(); aboutBackersTextView?.Dispose(); aboutBackersTextView = null;
        outputPanelState?.DisposeNativeText();
    }
}
