using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class AssetCreationPanel
{
    private Action<bool>? resolveWithoutSource;
    private string withoutSourceType = "";
    private bool openWithoutSource;
    private readonly EditorSelectableText withoutSourceMessage = new();
    internal void RequestWithoutSourcePrompt(string assetType, Action<bool> resolve)
    { withoutSourceType = assetType; resolveWithoutSource = resolve; openWithoutSource = true; }
    private void DrawWithoutSourcePrompt(Action<Action> execute)
    {
        var title = EditorLocalization.Widget("Stride Game Studio") + "###" + popupIdentity + "/WithoutSource";
        if (openWithoutSource) { Gui.OpenPopup(title); openWithoutSource = false; }
        using var modal = ImGuiScopes.Modal(title, ImGuiWindowFlags.AlwaysAutoResize);
        if (!modal.DrawContents) return;
        if (resolveWithoutSource == null) { Gui.CloseCurrentPopup(); return; }
        var message = string.Format(EditorLocalization.Text("Do you want to create this {0} without a source file?", "Message"), withoutSourceType);
        if (NativeMessageRenderer != null) NativeMessageRenderer("##WithoutSourceMessage", message);
        else withoutSourceMessage.Draw("##WithoutSourceMessage", message, true);
        if (Gui.Button(EditorLocalization.Widget("Create", "Button"))) Resolve(false, execute);
        Gui.SameLine();
        if (Gui.Button(EditorLocalization.Widget("Select a source...", "Button"))) Resolve(true, execute);
        Gui.SameLine();
        if (Gui.Button(EditorLocalization.Widget("Cancel", "Button")) || Gui.IsKeyPressed(ImGuiKey.Escape))
        { ClearWithoutSourcePrompt(); ReleaseAdapters(); Gui.CloseCurrentPopup(); }
    }
    private void Resolve(bool selectSource, Action<Action> execute)
    {
        var resolve = resolveWithoutSource!;
        ClearWithoutSourcePrompt(); Gui.CloseCurrentPopup();
        execute(() =>
        {
            try { resolve(selectSource); }
            finally { if (resolveWithoutSource == null) ReleaseAdapters(); }
        });
    }
    private void ClearWithoutSourcePrompt() { resolveWithoutSource = null; withoutSourceType = ""; openWithoutSource = false; }
}
