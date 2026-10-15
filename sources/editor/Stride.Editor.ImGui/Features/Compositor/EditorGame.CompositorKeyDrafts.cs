using Stride.Core.Assets.Quantum;
using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Quantum;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawRetainedCompositorDictionaryKey(AssetMetadataDocument source, IReadOnlyList<CompositorProperty> properties,
        CompositorAggregateProperty? aggregate = null)
    {
        var target = CaptureCompositorDraftTarget(source, properties, aggregate, source.CanRenameCompositorDictionaryKey);
        var fields = target.Fields;
        object?[] Read()
        {
            var values = fields.Select(field => field.Accessor.Index.Value).Concat(target.Read()).ToList();
            values.Add(source.EditRevision);
            foreach (var field in fields)
                foreach (var index in ((IAssetObjectNode)field.Accessor.Node).Indices ?? [])
                { values.Add(index); values.Add(((IAssetObjectNode)field.Accessor.Node).IndexToId(index)); values.Add(field.Accessor.Node.Retrieve(index)); }
            return values.ToArray();
        }
        var mixed = fields.Skip(1).Any(field => !Equals(field.Accessor.Index.Value, fields[0].Accessor.Index.Value));
        var feedback = Gui.GetID("##Key"); Gui.SetNextItemWidth(-1);
        try
        {
            PropertyGrid.Strings.Draw(fields[0].Accessor.Node, mixed, target.Editable, Read, text =>
            {
                ValidatePendingPropertyStrings(); source.CommitEdits();
                if (!target.Editable()) throw new InvalidOperationException("The compositor key target changed before validation.");
                UseTextureSettingsCommandContext();
                var actual = fields.DistinctBy(field => field.Accessor).ToArray();
                var owners = new List<EditorPresenterTarget>(); EditorPresenterTarget? combined = null; var transferred = false;
                try
                {
                    foreach (var field in actual)
                        if (source.AcquireCompositorFactoryTarget(field) is { } owner) owners.Add(owner);
                    if (owners.Count == actual.Length)
                    {
                        combined = new(owners);
                        var command = NodePresenterCombination.Commands(combined.Presenters).FirstOrDefault(command => command.Name == "RenameStringKey");
                        if (command != null)
                        {
                            if (combined.Presenters.Any(presenter => presenter.IsReadOnly)) throw new InvalidOperationException("The native dictionary key is read-only.");
                            StartEditorExtensionCommand(source, combined, command, text); transferred = true; return;
                        }
                        if (combined.Presenters.Any(presenter => presenter.Commands.Any(candidate => candidate.Name == "RenameStringKey")))
                            throw new InvalidOperationException("The registered key command does not support this native combination.");
                    }
                    else if (owners.SelectMany(owner => owner.Presenters).Any(presenter => presenter.Commands.Any(command => command.Name == "RenameStringKey")))
                        throw new InvalidOperationException("A selected native key presenter is no longer available.");
                    if (aggregate != null) source.RenameCompositorAggregateKeys(aggregate, text);
                    else source.RenameCompositorDictionaryKey(fields[0], text);
                }
                finally
                {
                    if (!transferred)
                    {
                        if (combined != null) Release(combined);
                        else foreach (var owner in owners) Release(owner);
                    }
                }
            }, target.Acquire, format: value => DictionaryKeyText(value!), controlLabel: "##Key", refreshAfterCommit: false, cancelOnLostFocus: true);
        }
        catch (Exception error) { PropertyConversionFeedback.Fail(feedback, error); statusMessage = error.Message; }
        PropertyConversionFeedback.Draw(feedback);
        void Release(EditorPresenterTarget owner)
        {
            try { owner.Dispose(); } catch { packageCommandCleanup.Add(owner.Dispose); throw; }
        }
    }
    private void DrawCompositorItemPresenterCommands(AssetMetadataDocument source, IReadOnlyList<CompositorProperty> properties)
    {
        var fields = properties.DistinctBy(field => field.Accessor).ToArray();
        var version = CompositorTypeCatalog.Version; var extensionVersion = source.SessionHistory?.Extensions?.Version;
        bool Current() => version == CompositorTypeCatalog.Version && extensionVersion == source.SessionHistory?.Extensions?.Version
            && sessionHistory?.Registry.Find(source.AuthoredAsset.Id) is { IsPublished: true, IsDisposed: false } owner
            && ReferenceEquals(owner.Document, source) && fields.Length != 0 && fields.All(source.CanCopyCompositorProperty);
        DrawNativePresenterCommands(source, Current,
            () => AcquireHierarchyFactoryTargets(fields.Length, index => source.AcquireCompositorFactoryTarget(fields[index])),
            () => new EditorAssetCommandMutation(source.BeginExtensionCommandMutation()));
    }
}
