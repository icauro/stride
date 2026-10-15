namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void ExecuteCompositorCommand(AssetMetadataDocument source, Action command, bool requiresEdit = true)
    {
        var providerVersion = CompositorTypeCatalog.Version;
        var extensionsVersion = source.SessionHistory?.Extensions?.Version;
        Execute(() =>
        {
            bool Current() => CanStartAssetEdit && providerVersion == CompositorTypeCatalog.Version
                && extensionsVersion == source.SessionHistory?.Extensions?.Version
                && sessionHistory?.Registry.Find(source.AuthoredAsset.Id) is { IsPublished: true, IsDisposed: false } owner
                && ReferenceEquals(owner.Document, source) && (!requiresEdit || source.CanEditCompositor);
            if (!Current()) throw new InvalidOperationException("The compositor command owner is no longer current.");
            using var lease = new RetainedPathLease(this, sessionHistory!.Registry.Find(source.AuthoredAsset.Id)!.AcquireView());
            BeforeNativeSelectionChange(); source.CommitEdits();
            if (!Current()) throw new InvalidOperationException("The compositor command owner changed during validation.");
            // Native document commands recheck current graph membership, ItemId/index,
            // factory/reference eligibility and sealed nodes after draft validation.
            UseTextureSettingsCommandContext(); command();
        });
    }
}
