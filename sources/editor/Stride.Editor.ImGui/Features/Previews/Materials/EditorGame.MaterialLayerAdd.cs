using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawNativeMaterialLayerAdd(AssetMetadataDocument owner, Action<Action> schedule)
    {
        using var disabled = ImGuiScopes.Disabled(!owner.CanAddMaterialLayer() || IsAuthoringPipelineUpdating);
        var supplied = owner.MaterialLayerAddEntries;
        if (supplied == null)
        {
            if (Gui.SmallButton("Add")) schedule(() => AddNativeMaterialLayer(owner, null));
        }
        else
        {
            Gui.SetNextItemWidth(UiScale(18));
            if (Gui.BeginCombo("##AddLayer", "Add", ImGuiComboFlags.NoPreview))
            {
                NativeAbstractEntryControl.Choices(supplied.ToArray(), null, entry => schedule(() => AddNativeMaterialLayer(owner, entry)));
                Gui.EndCombo();
            }
        }
    }
    private void AddNativeMaterialLayer(AssetMetadataDocument owner, AbstractNodeEntry? entry)
    {
        ValidatePendingPropertyStrings();
        if (IsAuthoringPipelineUpdating || !IsCurrentMetadataControlOwner(owner) || !owner.CanAddMaterialLayer()
            || sessionHistory?.Registry.Find(owner.AuthoredAsset.Id) is not { IsPublished: true } state || !ReferenceEquals(state.Document, owner))
            throw new InvalidOperationException("The material layer owner is no longer editable.");
        var entries = owner.MaterialLayerAddEntries;
        if (entry == null ? entries != null : entries == null || !NativeAbstractEntries.Contains(entries, entry))
            throw new InvalidOperationException("The material layer factory changed.");
        var target = owner.AcquireMaterialLayerAddTarget(); var transferred = false;
        try
        {
            var command = target?.Presenter.Commands.FirstOrDefault(command => command.Name == "AddNewItem");
            if (command != null) { StartEditorExtensionCommand(owner, target!, command, entry); transferred = true; }
            else owner.AddMaterialLayerEntry(entry);
        }
        finally
        {
            if (!transferred && target != null)
                try { target.Dispose(); } catch { packageCommandCleanup.Add(target.Dispose); throw; }
        }
    }
}
