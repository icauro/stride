using Hexa.NET.ImGui;
using Stride.Assets;
using Stride.Navigation;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private GameSettingsAsset? NavigationGameSettings()
    {
        var item = workspace?.Session.Packages.SelectMany(package => package.Assets)
            .FirstOrDefault(asset => asset.Asset.GetType() == typeof(GameSettingsAsset));
        if (item == null) return null;
        var state = SessionAuthoringHistory?.Registry.Find(item.Id);
        return state is { IsPublished: true, IsDisposed: false } && state.Document is IAssetTagDocument owner
            ? owner.AuthoredAsset as GameSettingsAsset : item.Asset as GameSettingsAsset;
    }
    private void DrawNavigationGroupValue(object? value, bool mixed, bool canEdit, Action<Guid> edit)
    {
        var settings = NavigationGameSettings();
        if (settings == null) { Gui.TextUnformatted("Not available"); return; }
        var groups = settings.GetOrDefault<NavigationSettings>().Groups;
        var current = value is Guid id ? id : Guid.Empty;
        var selected = groups.FirstOrDefault(group => group.Id == current);
        Gui.BeginDisabled(!canEdit);
        Gui.SetNextItemWidth(-1);
        if (Gui.BeginCombo("##NavigationGroup", mixed ? DifferentPropertyValues : selected?.Name ?? "None"))
        {
            if (Gui.Selectable(EditorLocalization.Widget("None", "Button"), !mixed && selected == null)) edit(Guid.Empty);
            foreach (var group in groups)
            {
                Gui.PushID(group.Id.ToString());
                if (Gui.Selectable(group.ToString(), !mixed && group.Id == current)) edit(group.Id);
                Gui.PopID();
            }
            Gui.EndCombo();
        }
        Gui.EndDisabled();
    }
    private void DrawNavigationGroup(ComponentField field)
    {
        Gui.PushID(field.Name); Gui.TextUnformatted(field.Label);
        DrawNavigationGroupValue(field.Node.Retrieve(), false, document!.CanEditComponentValue(field) && field.SupportsEditing,
            value => Execute(() => { document.UpdateComponent(field, value); document.EndComponentEdit(); }));
        Gui.PopID();
    }
}
