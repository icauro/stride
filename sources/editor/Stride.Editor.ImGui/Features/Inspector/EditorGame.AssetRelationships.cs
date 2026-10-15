using Hexa.NET.ImGui;
using Stride.Core.Assets;
using Stride.Core.Assets.Analysis;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawAssetRelationshipProperties(AssetMetadataDocument owner)
    {
        if (workspace == null) return;
        Execute(() => assetReferences.Update(workspace, documents, assetBrowser));
        var supplied = owner.ExtensionVirtualProperties.Select(presenter => presenter.Name).ToHashSet(StringComparer.Ordinal);
        if (owner.AuthoredAsset.Archetype is { } archetype && !supplied.Contains("ArchetypeVirtual"))
            DrawAssetRelationship("ArchetypeVirtual", "Archetype", archetype);
        if (owner.AuthoredAsset.Replaces is { } replaces && !supplied.Contains("ReplacesVirtual"))
            DrawAssetRelationship("ReplacesVirtual", "Replaces", replaces);
        if (!supplied.Contains("ReplacedByVirtual") && assetReferences.DependencySnapshot.TryGetValue(owner.AuthoredAsset.Id, out var dependencies))
        {
            var replacing = dependencies.LinksIn.Where(link => (link.Type & ContentLinkType.Replace) != 0 && link.Item != null && !link.Item.IsDeleted)
                .Select(link => link.Item).FirstOrDefault();
            if (replacing != null) DrawAssetRelationship("ReplacedByVirtual", "Replaced by", replacing.ToReference());
        }
    }

    private void DrawAssetRelationship(string id, string label, AssetReference reference)
    {
        Gui.PushID(id);
        try
        {
            if (!BeginInspectorPropertyRow(EditorLocalization.Text(label))) return;
            try
            {
                var target = workspace?.AssetItems.FirstOrDefault(item => item.Id == reference.Id && !item.IsDeleted);
                var text = target?.Location.ToString() ?? reference.Location;
                if (target == null) Gui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(0.95f, 0.32f, 0.32f, 1));
                try { DrawSelectableText("##Reference", text); }
                finally { if (target == null) Gui.PopStyleColor(); }
                if (target == null && Gui.IsItemHovered()) Gui.SetTooltip(EditorLocalization.Text("(Broken reference)"));
                if (!Gui.BeginPopupContextItem("##ReferenceActions")) return;
                try
                {
                    if (DrawNativeMenuItem(EditorLocalization.Text("Select the referenced asset"), "", false, target != null))
                        Execute(() =>
                        {
                            if (workspace != null && assetBrowser.RevealAsset(workspace, reference.Id) is { } entry)
                            { selectedAssetPath = entry.Path; ActivateAssetPropertyContext(); }
                        });
                }
                finally { Gui.EndPopup(); }
            }
            finally { Gui.EndTable(); }
        }
        finally { Gui.PopID(); }
    }
}
