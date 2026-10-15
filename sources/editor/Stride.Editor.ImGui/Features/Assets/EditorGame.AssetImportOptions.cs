using Stride.Assets.Models;
using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private AssetImportOptionsPanel? assetImportOptionsPanel;
    private void RequestAssetImportOptions(string[] files, Package package, string folder, Type? preferredType = null, bool resourceSourcesPrepared = false)
    {
        if (files.Length == 0) return;
        var owner = workspace!;
        var choices = files.Select(file => (File: Path.GetFullPath(file), Choices: GetAssetImportChoices(file, preferredType))).ToArray();
        if (choices.Any(file => file.Choices.Length == 0)) { RequestAssetImportFiles(files, package, folder, preferredType, resourceSourcesPrepared: resourceSourcesPrepared); return; }
        var skeletons = owner.AssetItems.Where(item => item.Asset is SkeletonAsset)
            .Select(item => (item.Id, item.Location.ToString())).ToArray();
        var initial = preferredType == typeof(ModelAsset) ? RestoreModelTemplateOptions(package, choices) : null;
        (assetImportOptionsPanel ??= new() { RecordControl = id => { if (Controls.Enabled) Controls.Record(id); }, OnCancelled = () => { if (!IsAssetImportUpdating) CancelPendingResourceCopies(); } }).Open(choices, skeletons, options =>
        {
            if (!ReferenceEquals(workspace, owner) || !CanAddAsset) throw new InvalidOperationException("The import target is no longer available.");
            if (preferredType == typeof(ModelAsset))
            {
                RememberModelTemplateOptions(package, options);
                RecordFbxTemplateOptionsAcceptance();
            }
            RequestAssetImportFiles(files, package, folder, preferredType, fileOptions: options, resourceSourcesPrepared: resourceSourcesPrepared);
        }, initial, preferredType);
    }
    private void DrawAssetImportOptionsDialog() { resourceSourceCopyPanel?.Draw(Execute, (id, text) => DrawSelectableText(id, text)); assetDropTemplatePanel?.Draw(Execute, PaintDroppedAssetTemplateRow); assetImportOptionsPanel?.Draw(Execute); }
    private void ClearAssetImportOptionsDialog() { StopResourceSourceCopies(); assetDropTemplatePanel?.Clear(); assetImportOptionsPanel?.Clear(); }
}
