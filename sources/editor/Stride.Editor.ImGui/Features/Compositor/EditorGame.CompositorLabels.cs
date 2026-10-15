using Stride.Core.Assets.Quantum;
using Gui = Hexa.NET.ImGui.ImGui;
using Hexa.NET.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static (bool Overridden, bool Inherited) CompositorLabelState(CompositorProperty property)
    {
        var owner = property.PresentationOwner ?? property;
        static bool Overridden(CompositorProperty? field) => field?.Accessor.Node switch
        {
            IAssetMemberNode member => member.IsContentOverridden(),
            IAssetObjectNode collection when field.Accessor.IsItem => collection.IsItemOverridden(field.Accessor.Index),
            _ => false,
        };
        static bool Inherited(CompositorProperty? field) => field == null || (field.Accessor.Node switch
        {
            IAssetMemberNode member => member.IsContentInherited(),
            IAssetObjectNode collection when field.Accessor.IsItem => collection.IsItemInherited(field.Accessor.Index),
            _ => false,
        });
        var hasBase = owner.Accessor.Node is IAssetNode { BaseNode: not null };
        return (Overridden(owner) || Overridden(owner.EnabledMember) || Overridden(owner.InlineMember),
            hasBase && Inherited(owner) && Inherited(owner.EnabledMember) && Inherited(owner.InlineMember));
    }
    private void DrawCompositorLabel(CompositorProperty property) => DrawCompositorLabel(property.Label, CompositorLabelState(property));
    private void DrawCompositorLabel(CompositorAggregateProperty property)
    {
        var overridden = false; var inherited = true;
        foreach (var field in property.Fields) { var state = CompositorLabelState(field); overridden |= state.Overridden; inherited &= state.Inherited; }
        DrawCompositorLabel(property.Label, (overridden, inherited));
    }
    private void DrawCompositorLabel(string label, (bool Overridden, bool Inherited) state)
    {
        var bold = PushPackagePropertiesBoldFont(state.Overridden);
        if (state.Inherited) { var color = Gui.GetStyle().Colors[(int)ImGuiCol.Text]; color.W *= .5f; Gui.PushStyleColor(ImGuiCol.Text, color); }
        try { DrawNativeLabel(label); }
        finally { if (state.Inherited) Gui.PopStyleColor(); if (bold) Gui.PopFont(); }
    }
}
