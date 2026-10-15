using Stride.Core.Presentation.Quantum.Presenters;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool TryDrawRegisteredProceduralProperty(ProceduralAssetDocument owner, AssetPropertyField field)
    {
        var extensions = owner.SessionHistory?.Extensions;
        if (extensions?.HasPrimitiveEditor(field.Node.Type) != true || field.Node.Retrieve() is Stride.Core.Yaml.IUnloadable) return false;
        Gui.PushID(field.Name);
        try
        {
            var label = owner.PropertyLabel(field.Node) ?? field.Label;
            if (!BeginInspectorPropertyRow(label)) return true;
            try
            {
                using var lease = new RetainedPathLease(this, AcquireRetainedProceduralNumber(owner,
                    () => owner.AcquireExtensionCommandTarget(field.Node)
                        ?? throw new InvalidOperationException("The procedural editor has no native presenter target.")));
                object?[] Read() => [field.Node.Retrieve(), field.Node, field.Node.Parent.Retrieve(), owner.Asset.Type,
                    owner.Path, owner.EditRevision, owner.SessionHistory?.Extensions?.Version,
                    sessionHistory?.Registry.Find(((IAssetTagDocument)owner).AuthoredAsset.Id)?.Generation];
                object?[] Target() => [field.Node, field.Node.Retrieve(), field.Node.Parent.Retrieve(), owner.Asset.Type, owner.Path,
                    owner.SessionHistory?.Extensions?.Version, sessionHistory?.Registry.Find(((IAssetTagDocument)owner).AuthoredAsset.Id)?.Generation];
                var expected = Read(); var rendering = true;
                bool Current() => rendering && !IsAuthoringPipelineUpdating && IsCurrentProceduralControlOwner(owner)
                    && owner.IsPropertyFieldCurrent(field) && expected.SequenceEqual(Read());
                void Validate()
                {
                    if (!Current()) throw new InvalidOperationException("The procedural editor context changed.");
                    var source = Target(); ValidatePendingPropertyStrings();
                    if (!rendering || IsAuthoringPipelineUpdating || !IsCurrentProceduralControlOwner(owner)
                        || !owner.IsPropertyFieldCurrent(field) || !source.SequenceEqual(Target()))
                        throw new InvalidOperationException("The procedural property changed during validation.");
                    expected = Read();
                }
                try
                {
                    extensions.TryDrawPrimitive(new(field.Node.Type, field.Node.Retrieve(), label,
                        Current() && owner.CanUpdateRegisteredProperty(field), value =>
                        {
                            Validate();
                            if (!owner.CanUpdateRegisteredProperty(field)) throw new InvalidOperationException("The procedural property became read-only.");
                            if (ContentAssetReference.AreEqual(field.Node.Retrieve(), value)) return;
                            owner.UpdateRegisteredProperty(field, value); expected = Read();
                        }, () => { Validate(); owner.EndRegisteredPropertyEdit(); expected = Read(); }, (name, parameter) =>
                        {
                            Validate(); owner.EndRegisteredPropertyEdit(); expected = Read();
                            var target = owner.AcquireExtensionCommandTarget(field.Node)
                                ?? throw new InvalidOperationException("The procedural command target changed.");
                            var transferred = false;
                            try
                            {
                                var command = NodePresenterCombination.Commands(target.Presenters)
                                    .SingleOrDefault(command => command.Name == name && command.CanExecute(target.Presenters, parameter))
                                    ?? throw new InvalidOperationException("The native procedural command is not available.");
                                transferred = true;
                                StartEditorExtensionCommand(owner, target, command, parameter,
                                    () => new EditorAssetCommandMutation(owner.BeginExtensionCommandMutation()));
                            }
                            finally
                            {
                                if (!transferred)
                                    try { target.Dispose(); } catch { packageCommandCleanup.Add(target.Dispose); throw; }
                            }
                        }));
                }
                finally { rendering = false; }
            }
            finally { Gui.EndTable(); }
            DrawProceduralBaseReset(owner, field.Node);
            return true;
        }
        catch (Exception error) { assetEditError = error.Message; statusMessage = error.Message; return true; }
        finally { Gui.PopID(); }
    }
}
