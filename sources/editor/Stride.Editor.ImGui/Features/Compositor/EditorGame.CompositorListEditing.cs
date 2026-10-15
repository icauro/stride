using Hexa.NET.ImGui;
using Stride.Rendering;
using Stride.Rendering.Compositing;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void BeginCompositorListNameEdit(AssetMetadataDocument document, CompositorEditorState state, CompositorListItem item)
    {
        ExecuteCompositorCommand(document, () =>
        {
            if (!document.CanRenameCompositorListItem(item)) throw new InvalidOperationException("The compositor name target is no longer current.");
            state.EditingListItem = item.ItemId; state.EditingListCollection = item.Collection;
            state.EditingListOriginal = item.Name; state.FocusListName = true;
        });
    }
    private bool DrawCompositorListNameEdit(AssetMetadataDocument document, CompositorEditorState state, CompositorListItem item)
    {
        if (state.EditingListItem != item.ItemId || state.EditingListCollection != item.Collection) return false;
        var member = item.Node[nameof(RenderStage.Name)]; var providerVersion = CompositorTypeCatalog.Version;
        var expectedName = state.EditingListOriginal;
        bool Editable() => CanStartAssetEdit && providerVersion == CompositorTypeCatalog.Version
            && sessionHistory?.Registry.Find(document.AuthoredAsset.Id) is { IsPublished: true, IsDisposed: false } owner
            && ReferenceEquals(owner.Document, document) && document.CanRenameCompositorListItem(item);
        object?[] Read() => [item.Name, item.Node, item.Value, item.Collection, item.ItemId,
            document.AuthoringGraph.RootNode[item.Collection].Target, document.SessionHistory?.Extensions?.Version];
        if (state.FocusListName) { Gui.SetKeyboardFocusHere(); state.FocusListName = false; }
        Gui.SetNextItemWidth(-1);
        var feedback = Gui.GetID("##Name");
        try
        {
            PropertyGrid.Strings.Draw(member, false, Editable, Read, name =>
            {
                UseTextureSettingsCommandContext(); document.RenameCompositorListItem(item, name, expectedName);
                state.EditingListItem = null;
            }, () =>
            {
                if (!Editable()) throw new InvalidOperationException("The compositor name owner changed.");
                var leases = new List<IDisposable>();
                try
                {
                    leases.Add(sessionHistory!.Registry.Find(document.AuthoredAsset.Id)!.AcquireView());
                    if (document.AcquireExtensionCommandTarget(member) is { } target) leases.Add(target);
                    return new PropertyStringOwners(leases, [document]);
                }
                catch { foreach (var lease in leases.AsEnumerable().Reverse()) lease.Dispose(); throw; }
            }, controlLabel: "##Name");
        }
        catch (Exception error) { PropertyConversionFeedback.Fail(feedback, error); statusMessage = error.Message; }
        if (Gui.IsItemActive() && Gui.IsKeyPressed(ImGuiKey.Escape))
        { PropertyGrid.Strings.Abandon(member, feedback); state.EditingListItem = null; }
        else if (Gui.IsItemDeactivated() && !PropertyGrid.Strings.IsPending(member, feedback)) state.EditingListItem = null;
        PropertyConversionFeedback.Draw(feedback);
        return true;
    }
    private void DrawCompositorListItemName(CompositorListItem item, NVector2 position)
    {
        var bold = PushPackagePropertiesBoldFont(item.Value is RenderStage or SceneCameraSlot);
        var name = item.Value is RenderStage or SceneCameraSlot ? item.Name : item.Value.GetType().Name;
        var width = Gui.CalcTextSize(name).X;
        try { Gui.GetWindowDrawList().AddText(position, Gui.GetColorU32(ImGuiCol.Text), name); }
        finally { if (bold) Gui.PopFont(); }
        if (item.Value is RenderStage)
            Gui.GetWindowDrawList().AddText(position + new NVector2(width, 0), Gui.GetColorU32(ImGuiCol.TextDisabled), item.SlotLabel);
    }
}
