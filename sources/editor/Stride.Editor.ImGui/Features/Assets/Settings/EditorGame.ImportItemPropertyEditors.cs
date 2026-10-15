using Stride.Core.Presentation.Quantum.Presenters;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool TryDrawRegisteredImportItem(AssetMetadataDocument owner, AssetImportSettingItem item)
    {
        var extensions = owner.SessionHistory?.Extensions;
        var declaredType = item.Field.ElementType!;
        if (extensions?.HasPrimitiveEditor(declaredType) != true) return false;
        var editable = CanEditImportItemDraft(owner, item);
        using var disabled = ImGuiScopes.Disabled(!editable);
        extensions.TryDrawPrimitive(new(declaredType, owner.ReadImportItem(item), "##Value", editable,
            value => { RequireCurrent(); ValidatePendingPropertyStrings(); RequireCurrent(); UseTextureSettingsCommandContext(); owner.UpdateImportItem(item, value); },
            () => { RequireCurrent(); UseTextureSettingsCommandContext(); owner.EndImportSettingEdit(); },
            (name, parameter) =>
            {
                RequireCurrent(); ValidatePendingPropertyStrings(); owner.EndImportSettingEdit(); RequireCurrent();
                var target = owner.AcquireImportItemCommandTarget(item)
                    ?? throw new InvalidOperationException("The actual import item command target is no longer available.");
                var transferred = false;
                try
                {
                    var command = NodePresenterCombination.Commands(target.Presenters).SingleOrDefault(command => command.Name == name)
                        ?? throw new InvalidOperationException("The native item command is not registered for this target.");
                    transferred = true; StartEditorExtensionCommand(owner, target, command, parameter);
                }
                finally
                {
                    if (!transferred)
                        try { target.Dispose(); } catch { packageCommandCleanup.Add(target.Dispose); throw; }
                }
            }));
        return true;
        void RequireCurrent()
        {
            if (!editable || !CanEditImportItemDraft(owner, item)) throw new InvalidOperationException("The import item is currently read-only.");
        }
    }
}
