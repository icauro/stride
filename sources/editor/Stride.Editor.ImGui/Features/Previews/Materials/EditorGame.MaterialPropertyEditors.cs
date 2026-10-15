using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Quantum;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool TryDrawRegisteredMaterialProperty(AssetMetadataDocument owner, MaterialSettingField field, string label)
        => TryDrawRegisteredAssetSetting(owner, field.Node, owner.ReadMaterialSetting(field), label,
            field.CanAssign && owner.CanEditMaterialSettings && owner.CanEditAssetSettingNode(field.Node) && !owner.IsMaterialSettingReadOnly(field),
            value => owner.UpdateMaterialSetting(field, value), owner.EndMaterialSettingEdit);

    private bool TryDrawRegisteredSkyboxProperty(AssetMetadataDocument owner, SkyboxSettingField field)
        => TryDrawCertifiedPropertyEditor(owner, field.Node, owner.SkyboxSettingLabel(field),
            () => IsCertifiedPropertyOwner(owner) && owner.SkyboxSettingsFields.Contains(field),
            () => field.CanAssign && owner.CanEditSkyboxSettings && owner.CanEditAssetSettingNode(field.Node) && !owner.IsSkyboxSettingReadOnly(field),
            () => owner.ReadSkyboxSetting(field),
            () => [.. CertifiedPropertyTarget(owner, field.Node), owner.ReadSkyboxSetting(field), owner.EditRevision],
            () => CertifiedPropertyTarget(owner, field.Node),
            value => owner.UpdateSkyboxSetting(field, value), owner.EndSkyboxSettingEdit);

    private bool TryDrawRegisteredAssetSetting(AssetMetadataDocument owner, IMemberNode member, object? documentValue, string label,
        bool canEdit, Action<object?> stage, Action finish)
    {
        var extensions = owner.SessionHistory?.Extensions;
        if (extensions?.HasPrimitiveEditor(member.Type) != true) return false;
        var editable = canEdit && !IsAuthoringPipelineUpdating;
        Gui.BeginDisabled(!editable);
        try
        {
            extensions.TryDrawPrimitive(new(member.Type, documentValue, label, editable,
                value => { RequireEditable(); UseTextureSettingsCommandContext(); stage(value); },
                () => { RequireEditable(); UseTextureSettingsCommandContext(); finish(); },
                (name, parameter) =>
                {
                    RequireEditable();
                    finish();
                    using var target = owner.AcquireExtensionCommandTarget(member)
                        ?? throw new InvalidOperationException("The actual property command target is no longer available.");
                    var command = NodePresenterCombination.Commands(target.Presenters).SingleOrDefault(command => command.Name == name)
                        ?? throw new InvalidOperationException("The native property command is not registered for this target.");
                    StartEditorExtensionCommand(owner, member, command, parameter);
                }));
        }
        finally { Gui.EndDisabled(); }
        return true;
        void RequireEditable()
        {
            if (!editable || IsAuthoringPipelineUpdating || !owner.CanEditAssetSettingNode(member) || owner.IsExtensionPropertyReadOnly(member))
                throw new InvalidOperationException("This authored property is currently read-only.");
        }
    }
}
