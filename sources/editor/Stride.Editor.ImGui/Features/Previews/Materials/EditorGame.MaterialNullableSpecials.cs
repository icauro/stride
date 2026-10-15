using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawMaterialNullableSpecialPresence(AssetMetadataDocument owner, MaterialSettingField field)
    {
        var target = owner.AcquireExtensionCommandTarget(field.Node); var transferred = false;
        try
        {
            var present = owner.ReadMaterialSetting(field) != null;
            AbstractNodeEntry entry = present ? AbstractNodeValue.Null : new AbstractNodeType(field.Type);
            var command = target?.Presenter.Commands.FirstOrDefault(command => command.Name == "CreateNewInstance");
            if (command == null) return;
            using var disabled = ImGuiScopes.Disabled(!CanEditMaterialSettingDraft(owner, field)
                || target!.Presenters.Any(presenter => presenter.IsReadOnly) || !command.CanExecute(target.Presenters, entry));
            if (DrawNativeNullablePresence(present, out _, field.AllowsNull))
            {
                if (!CanEditMaterialSettingDraft(owner, field)) throw new InvalidOperationException("The material setting is no longer editable.");
                UseTextureSettingsCommandContext();
                StartEditorExtensionCommand(owner, target!, command, entry,
                    () => new EditorAssetCommandMutation(owner.BeginExtensionCommandMutation()));
                transferred = true;
            }
        }
        finally
        {
            if (!transferred && target != null)
                try { target.Dispose(); } catch { packageCommandCleanup.Add(target.Dispose); throw; }
        }
    }
}
