using Stride.Core.Presentation.Quantum.Presenters;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool TryDrawRegisteredFontItem(AssetMetadataDocument owner, FontSettingField field, FontSettingItem item)
    {
        var extensions = owner.SessionHistory?.Extensions;
        var declared = (field.Collection?.Descriptor as Stride.Core.Reflection.CollectionDescriptor)?.ElementType;
        if (declared == null || extensions?.HasPrimitiveEditor(declared) != true) return false;
        var target = owner.AcquireFontItemCommandTarget(field, item)
            ?? throw new InvalidOperationException("The font item presenter changed.");
        var leases = new List<IDisposable> { target };
        try { leases.Add(AcquirePropertyStringOwner(owner)); }
        catch
        {
            var failed = new PropertyStringOwners(leases, [owner]);
            try { failed.Dispose(); } catch { packageCommandCleanup.Add(failed.Dispose); }
            throw;
        }
        using var held = new RetainedPathLease(this, new PropertyStringOwners(leases, [owner]));
        var node = target.Presenter; var rendering = true;
        object?[] Target() => [node.GetNodeAccessor().Node, node.GetNodeAccessor().Index, node.Value, field.Node, field.Key,
            field.Node.Parent.Retrieve(), field.Collection, item.Id, owner.Path,
            node.Value is DateTime date ? date.Kind : null,
            sessionHistory?.Registry.Find(owner.AuthoredAsset.Id)?.Generation, extensions.Version];
        object?[] Read() => [.. Target(), owner.EditRevision];
        var expected = Read();
        bool Current() => rendering && !IsAuthoringPipelineUpdating && IsCurrentMetadataControlOwner(owner)
            && owner.IsCurrentFontItemProperty(field, item.Id, item.Index, node) && expected.SequenceEqual(Read());
        void Validate()
        {
            if (!Current()) throw new InvalidOperationException("The font item editor context changed.");
            var before = Target(); ValidatePendingPropertyStrings();
            if (!rendering || IsAuthoringPipelineUpdating || !IsCurrentMetadataControlOwner(owner)
                || !owner.IsCurrentFontItemProperty(field, item.Id, item.Index, node) || !before.SequenceEqual(Target()))
                throw new InvalidOperationException("The font item changed during pending validation.");
            expected = Read();
        }
        try
        {
            extensions.TryDrawPrimitive(new(declared, node.Value, "##FontItemValue",
                Current() && owner.CanEditFontItemProperty(field, item.Id, item.Index, node), value =>
                {
                    if (!Current() || !owner.CanEditFontItemProperty(field, item.Id, item.Index, node)) throw new InvalidOperationException("The font item is read-only or changed.");
                    if (ContentAssetReference.AreEqual(node.Value, value)) return;
                    Validate(); UseTextureSettingsCommandContext(); owner.UpdateFontItemProperty(field, item.Id, item.Index, node, value); expected = Read();
                }, () => { if (!rendering) throw new InvalidOperationException("The font item editor context expired."); }, (name, parameter) =>
                {
                    if (!Current()) throw new InvalidOperationException("The font item command context changed.");
                    var command = NodePresenterCombination.Commands(target.Presenters)
                        .SingleOrDefault(candidate => candidate.Name == name && candidate.CanExecute(target.Presenters, parameter))
                        ?? throw new InvalidOperationException("The native font item command is not available.");
                    Validate();
                    var actorTarget = owner.AcquireExtensionFactoryTarget(node.GetNodeAccessor())
                        ?? throw new InvalidOperationException("The font item command target changed.");
                    var transferred = false;
                    try
                    {
                        if (!NodePresenterCombination.Commands(actorTarget.Presenters).Contains(command) || !command.CanExecute(actorTarget.Presenters, parameter))
                            throw new InvalidOperationException("The font item command changed during pending validation.");
                        transferred = true; StartEditorExtensionCommand(owner, actorTarget, command, parameter);
                    }
                    finally { if (!transferred) ReleaseHierarchyFactoryTarget(actorTarget); }
                }));
        }
        finally { rendering = false; }
        return true;
    }
}
