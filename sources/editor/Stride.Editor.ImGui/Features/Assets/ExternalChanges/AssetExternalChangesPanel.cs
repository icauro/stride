using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal interface IAssetExternalChangesHost
{
    bool IsDirty(string path);
    void Reload(ExternalAssetChange change);
    void DrawSelectableText(string id, string value);
    void ReportFailure(Exception error);
}
internal sealed class AssetExternalChangesPanel(IAssetExternalChangesHost host)
{
    internal Action<string>? WorkflowControlRecorder { get; set; }
    private bool open;
    private string? error;
    private long observedVersion;
    public void Draw(AssetExternalChangeMonitor monitor)
    {
        var changes = monitor.Changes;
        if (changes.Count != 0 && monitor.ChangeVersion != observedVersion) open = true;
        observedVersion = monitor.ChangeVersion;
        // Background file notifications must not replace an in-progress modal
        // such as dirty-close or project replacement. Keep the request pending.
        if (open && !Gui.IsAnyItemActive()
            && !Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel))
        {
            Gui.OpenPopup("External asset changes"); open = false;
        }
        using var modal = ImGuiScopes.Modal("External asset changes", ImGuiWindowFlags.AlwaysAutoResize);
        if (!modal.DrawContents) return;
        foreach (var change in changes)
        {
            Gui.PushID(change.Asset.UiId); host.DrawSelectableText("##Path", change.Asset.Path);
            if (change.Missing) Gui.TextUnformatted("Missing file");
            else
            {
                var dirty = host.IsDirty(change.Asset.Path);
                if (Gui.Button(dirty ? "Discard and reload" : "Reload"))
                {
                    try { host.Reload(change); monitor.Reloaded(change); error = null; }
                    catch (Exception failure) { error = failure.Message; host.ReportFailure(failure); }
                }
                Gui.SameLine();
            }
            var keep = Gui.Button("Keep");
            WorkflowControlRecorder?.Invoke("external-change/keep");
            if (keep) monitor.Keep(change);
            Gui.PopID();
        }
        if (error != null) host.DrawSelectableText("##Error", error);
        if (monitor.Changes.Count == 0 || Gui.Button(EditorLocalization.Widget("Close", "Button"))) Gui.CloseCurrentPopup();
    }
}
