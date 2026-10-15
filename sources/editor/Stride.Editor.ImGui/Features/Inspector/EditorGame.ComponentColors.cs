using Hexa.NET.ImGui;
using Stride.Core.Assets.Editor.ViewModel;
using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private ComponentField? activeColorPopup;
    private readonly Dictionary<ComponentField, object> inheritedColorDrafts = [];
    private static readonly string[] ColorChannelLabels = ["R", "G", "B", "A"];
    private void DrawComponentColor(ComponentField field, string? label = null)
    {
        var owner = document!;
        var inherited = field.Node is Stride.Core.Assets.Quantum.IAssetMemberNode { BaseNode: not null };
        bool Editable() => ReferenceEquals(document, owner) && !IsAuthoringPipelineUpdating
            && field.Component.Entity is { } entity && owner.IsSelected(entity.Id)
            && (inherited ? owner.CanOverrideInheritedComponent(field) : owner.CanEditComponentValue(field));
        Gui.PushID(field.Name);
        try
        {
            using var nullable = NativeNullableRow.Begin("##Nullable", inherited && field.IsNullable);
            if (inherited && field.IsNullable)
            {
                var present = field.Node.Retrieve() != null;
                using (ImGuiScopes.Disabled(!Editable()))
                    if (DrawNativeNullablePresence(present, out var create, field.AllowsNullObject)) Execute(() => owner.EditInheritedScalar(field, create ? CreateNullableValue(field.Type) : null));
                nullable.ValueColumn();
                if (field.Node.Retrieve() == null) { DrawInheritedPropertyLabel(field, label); Gui.SameLine(); Gui.TextUnformatted(EditorLocalization.Text("(null)")); DrawInheritedResetAction(field); return; }
            }
            var control = "component/" + field.Component.Id + "/" + field.Name;
            var open = Gui.TreeNodeEx("##Channels", ImGuiTreeNodeFlags.None);
            if (Controls.Enabled) { Controls.Record(control + "/expand"); Controls.Expanded[control + "/expand"] = open; }
            Gui.SameLine();
            if (inherited) DrawInheritedPropertyLabel(field, label); else DrawDocumentedComponentLabel(field, label);
            Gui.SameLine();
            DrawRetainedColor(field.Node, field.Type, () => [field.Node.Retrieve()], Editable, create =>
            {
                var value = create(field.Node.Retrieve());
                if (inherited) owner.EditInheritedScalar(field, value);
                else { owner.UpdateComponent(field, value); owner.EndComponentEdit(); }
                componentErrors.Remove(field);
            }, () => AcquirePropertyStringOwners([owner]), axis => !field.IsAxisReadOnly(axis),
                inherited ? () => DrawInheritedResetAction(field) : null, Controls.Enabled ? part => Controls.Record(control + "/" + part) : null);
            if (componentErrors.TryGetValue(field, out var message)) DrawSelectableText("##ColorError", message);
            if (open) { DrawComponentFields(field.Children); Gui.TreePop(); }
        }
        finally { Gui.PopID(); }
    }
    private void CommitInheritedColorDraft(ComponentField field)
    {
        if (!inheritedColorDrafts.Remove(field, out var value) || document?.CanOverrideInheritedComponent(field) != true) return;
        try { document!.EditInheritedScalar(field, value); componentErrors.Remove(field); }
        catch (Exception failure) { componentErrors[field] = failure.Message; statusMessage = failure.Message; }
    }
}
