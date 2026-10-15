using Stride.Assets.Models;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    public bool HasSkeletonPreservationActions(AssetImportSettingField field)
        => field.Collection?.Retrieve() is ICollection<NodeInformation>;

    private AssetImportSettingField[] SkeletonPreservationFields(AssetImportSettingField field)
        => field.Items.SelectMany(item => item.Children)
            .Where(child => child.Node.Name == nameof(NodeInformation.Preserve) && child.Type == typeof(bool)).ToArray();

    public bool CanSetSkeletonPreservation(AssetImportSettingField field)
    {
        if (!HasSkeletonPreservationActions(field)) return false;
        var fields = SkeletonPreservationFields(field);
        return fields.Length == field.Items.Count && field.Items.All(CanEditImportItem) && fields.All(CanEditImportSetting);
    }

    public void SetSkeletonPreservation(AssetImportSettingField field, bool preserve)
    {
        CommitEdits();
        if (!CanSetSkeletonPreservation(field)) throw new InvalidOperationException("This asset setting cannot be edited.");
        var fields = SkeletonPreservationFields(field);
        if (fields.All(child => Equals(child.Node.Retrieve(), preserve))) return;
        ApplyInheritedAssetMutation(() =>
        {
            foreach (var child in fields)
                if (!Equals(child.Node.Retrieve(), preserve)) child.Node.Update(preserve);
        }, preserve ? "Preserve all skeleton nodes" : "Preserve no skeleton nodes");
    }
}
