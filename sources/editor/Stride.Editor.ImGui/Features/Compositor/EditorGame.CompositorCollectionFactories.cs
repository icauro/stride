using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;
using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Quantum;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawNativeCompositorCollectionAdd(AssetMetadataDocument source, IReadOnlyList<CompositorProperty> fields, Action<Action> defer)
    {
        var target = CaptureCompositorDraftTarget(source, fields, canEditField: source.CanNativeCompositorCollectionAdd);
        var metadata = target.Fields.Select(source.CompositorCollectionNativeEntries).ToArray();
        using (ImGuiScopes.Disabled(!target.Editable()))
            if (Gui.SmallButton("+"))
            {
                if (metadata.All(entries => entries == null)) defer(() => Add(null));
                else Gui.OpenPopup("Add item");
            }
        if (!Gui.BeginPopup("Add item")) return;
        try
        {
            var entries = NativeAbstractEntries.Combine(metadata.Select(entries => entries ?? []).ToArray());
            NativeAbstractEntryControl.Choices(entries, null, entry => defer(() => Add(entry)));
        }
        finally { Gui.EndPopup(); }
        void Add(AbstractNodeEntry? entry)
        {
            if (!target.Editable() || target.Fields.Any(field => entry == null ? source.CompositorCollectionNativeEntries(field) != null
                : source.CompositorCollectionNativeEntries(field) is not { } entries || !NativeAbstractEntries.Contains(entries, entry)))
                throw new InvalidOperationException("The native compositor collection entry is no longer current.");
            using var lease = new RetainedPathLease(this, target.Acquire());
            var uniqueFields = target.Fields.DistinctBy(field => field.Collection).ToArray();
            var owners = new List<EditorPresenterTarget>(); EditorPresenterTarget? combined = null; var transferred = false;
            try
            {
                foreach (var field in uniqueFields)
                    if (source.AcquireCompositorFactoryTarget(field) is { } owner) owners.Add(owner);
                if (owners.Count == uniqueFields.Length)
                {
                    combined = new(owners);
                    var command = NodePresenterCombination.Commands(combined.Presenters).FirstOrDefault(command => command.Name == "AddNewItem");
                    if (command != null)
                    {
                        if (combined.Presenters.Any(presenter => presenter.IsReadOnly)) throw new InvalidOperationException("The native collection is read-only.");
                        StartEditorExtensionCommand(source, combined, command, entry); transferred = true; return;
                    }
                    if (combined.Presenters.Any(presenter => presenter.Commands.Any(candidate => candidate.Name == "AddNewItem")))
                        throw new InvalidOperationException("The registered collection command does not support this native combination.");
                }
                else if (owners.SelectMany(owner => owner.Presenters).Any(presenter => presenter.Commands.Any(command => command.Name == "AddNewItem")))
                    throw new InvalidOperationException("A selected native collection presenter is no longer available.");
                source.AddNativeCompositorCollectionItems(target.Fields, entry);
            }
            finally
            {
                if (!transferred)
                {
                    if (combined != null) Release(combined);
                    else foreach (var owner in owners) Release(owner);
                }
            }
        }
        void Release(EditorPresenterTarget owner)
        {
            try { owner.Dispose(); } catch { packageCommandCleanup.Add(owner.Dispose); throw; }
        }
    }
}
