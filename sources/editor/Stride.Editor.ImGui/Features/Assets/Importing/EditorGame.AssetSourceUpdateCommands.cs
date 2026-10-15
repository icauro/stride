using Stride.Core.Assets;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private SourceReimportModifiedState? modifiedSourceStatus;
    private void ClearModifiedSourceStatus() { modifiedSourceStatus?.Clear(); modifiedSourceStatus = null; }
    private void RefreshModifiedSourceStatus()
    {
        if (!CanStartSourceReimport) return;
        var status = modifiedSourceStatus ??= new();
        if (!status.NeedsSnapshot(workspace!)) return;
        var certificates = new List<SourceReimportCertificate>();
        foreach (var entry in workspace!.Assets.Where(HasReimportSources))
        {
            var asset = authoring?.Find(entry.Id)?.Document is IAssetTagDocument document ? document.AuthoredAsset : workspace.FindAsset(entry.Id).Asset;
            try { certificates.Add(CaptureSourceReimportCertificate(entry, asset)); }
            catch (InvalidOperationException) { }
        }
        status.Start(certificates.ToArray());
    }
    private bool CanReimportAllModifiedAssets
    {
        get { RefreshModifiedSourceStatus(); return CanStartSourceReimport && modifiedSourceStatus?.Modified.Count > 0; }
    }
    private int ModifiedSourceAssetCount
    {
        get { _ = CanReimportAllModifiedAssets; return modifiedSourceStatus?.Modified.Count ?? 0; }
    }
    private void ProcessAssetSourceUpdateShortcuts()
    {
        if (!CanStartSourceReimport || Gui.GetIO().AppFocusLost || !Gui.GetIO().KeyCtrl || Gui.GetIO().KeyAlt || Gui.GetIO().WantTextInput || Gui.IsAnyItemActive()
            || Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel) || !Gui.IsKeyPressed(ImGuiKey.R, false)) return;
        if (Gui.GetIO().KeyShift)
        {
            var seed = assetBrowser.SelectedEntries.ToArray();
            if (CanReimportAssets(seed)) Execute(() => RequestAssetReimports(seed));
        }
        else if (CanReimportAllModifiedAssets) Execute(RequestAllModifiedAssetReimports);
    }
    private void RequestAllModifiedAssetReimports()
    {
        if (!CanStartSourceReimport) return;
        var selection = workspace!.Assets.Where(HasReimportSources).ToArray();
        if (selection.Length != 0) RequestAssetReimports(selection, true);
    }
}
