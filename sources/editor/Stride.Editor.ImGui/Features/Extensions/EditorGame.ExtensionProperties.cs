using Stride.Core.Assets;
using Stride.Core.Quantum;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool TryDrawRegisteredComponentProperty(ComponentField field)
    {
        var extensions = document?.SessionHistory?.Extensions;
        if (document == null || extensions?.HasPrimitiveEditor(field.DeclaredType) != true) return false;
        Gui.PushID(field.Name);
        try
        {
            if (!BeginInspectorPropertyRow(field.Label)) return true;
            try
            {
                var owner = document;
                var editable = !IsAuthoringPipelineUpdating && owner.CanEditExtensionProperty(field);
                Gui.BeginDisabled(!editable);
                try
                {
                    extensions.TryDrawPrimitive(new(field.DeclaredType, field.Node.Retrieve(), field.Label, editable,
                        value => owner.UpdateExtensionProperty(field, value), () => owner.EndEdits()));
                }
                finally { Gui.EndDisabled(); }
            }
            finally { Gui.EndTable(); }
        }
        finally { Gui.PopID(); }
        return true;
    }
    private bool TryDrawRegisteredImportProperty(AssetMetadataDocument owner, AssetImportSettingField field)
    {
        object?[] Target() => [.. CertifiedPropertyTarget(owner, field.Node), field.Key,
            owner.AuthoredAsset is Stride.Video.Assets.VideoAsset video ? video.IsSizeInPercentage : null];
        var drawn = TryDrawCertifiedPropertyEditor(owner, field.Node, owner.ExtensionPropertyLabel(field.Node) ?? field.Label,
            () => IsCertifiedPropertyOwner(owner) && owner.IsCurrentImportSettingField(field), () => owner.CanEditImportSetting(field),
            () => owner.ReadImportSetting(field), () => [.. Target(), owner.ReadImportSetting(field), owner.EditRevision], Target,
            value => owner.UpdateImportSetting(field, value), owner.EndImportSettingEdit);
        if (drawn) DrawAssetSettingResetToBase(owner, field.Node);
        return drawn;
    }
    private void DrawRegisteredPropertyCommands(IAssetTagDocument owner, IGraphNode node, bool canEdit, Action<Action, string> mutate)
    {
        var extensions = (owner as ISessionAuthoringDocument)?.SessionHistory?.Extensions;
        if (extensions == null) return;
        foreach (var command in extensions.PropertyCommands(owner.AuthoredAsset, node.Type))
        {
            var enabled = canEdit && !IsAuthoringPipelineUpdating && command.CanExecute(owner.AuthoredAsset, node);
            if (Gui.MenuItem(command.Name, "", false, enabled))
                Execute(() => mutate(() => command.Execute(owner.AuthoredAsset, node), command.Name));
        }
        if (node is IMemberNode member)
        {
            using var target = owner switch
            {
                AssetMetadataDocument metadata => metadata.AcquireExtensionCommandTarget(member),
                EntityHierarchyDocument hierarchy => hierarchy.AcquireExtensionCommandTarget(member),
                _ => null,
            };
            if (target != null)
                foreach (var command in target.Presenter.Commands)
                    if (Gui.MenuItem(command.Name, "", false, !IsAuthoringPipelineUpdating && command.CanExecute([target.Presenter], null)))
                        Execute(() => StartEditorExtensionCommand(owner, node, command));
        }
    }
}
