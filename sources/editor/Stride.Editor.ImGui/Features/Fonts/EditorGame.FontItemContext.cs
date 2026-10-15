using Stride.Core.Presentation.Quantum.Presenters;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawFontItemContext(AssetMetadataDocument owner, FontSettingField member, FontSettingItem item,
        bool headerHovered, Action<Action> schedule)
    {
        if ((headerHovered || Gui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) && Gui.IsMouseReleased(ImGuiMouseButton.Right))
            Gui.OpenPopup("##FontItemActions");
        if (!Gui.BeginPopup("##FontItemActions")) return;
        try
        {
            if (DrawNativeMenuItem(EditorLocalization.Widget("Copy", "Menu"), "", false, owner.CanCopyFontItem(member, item)))
                Execute(() => Gui.SetClipboardText(owner.CopyFontItem(member, item)));
            var text = Gui.GetClipboardTextS() ?? "";
            var canPaste = !IsAuthoringPipelineUpdating && owner.CanPasteFontItem(member, item, text);
            if (DrawNativeMenuItem(EditorLocalization.Widget("Paste", "Menu"), "", false, canPaste)) schedule(() => owner.PasteFontItem(member, item, text, false));
            if (DrawNativeMenuItem("Replace", "", false, canPaste)) schedule(() => owner.PasteFontItem(member, item, text, true));
            if (DrawNativeMenuItem(EditorLocalization.Widget("Reset to base value", "Menu"), "", false, !IsAuthoringPipelineUpdating && owner.CanResetFontItem(member, item)))
                schedule(() => owner.ResetFontItem(member, item));
            var target = owner.AcquireFontItemCommandTarget(member, item);
            if (target == null) return;
            try
            {
                if (!owner.IsFontItemCommandTargetCurrent(member, item, target)) return;
                foreach (var command in NodePresenterCombination.Commands(target.Presenters))
                    if (DrawNativeMenuItem(command.Name, "", false,
                        !IsAuthoringPipelineUpdating && command.CanExecute(target.Presenters, null)))
                        Execute(() => StartFontItemCommand(owner, member, item, command));
            }
            finally
            {
                try { target.Dispose(); }
                catch { packageCommandCleanup.Add(target.Dispose); throw; }
            }
        }
        finally { Gui.EndPopup(); }
    }
    private void StartFontItemCommand(AssetMetadataDocument owner, FontSettingField member, FontSettingItem item, INodePresenterCommand command)
    {
        if (IsAuthoringPipelineUpdating) throw new InvalidOperationException("The editor cannot start another native command.");
        ValidatePendingPropertyStrings();
        owner.CommitEdits();
        var target = owner.AcquireFontItemCommandTarget(member, item)
            ?? throw new InvalidOperationException("The authored font item is no longer available.");
        var transferred = false;
        try
        {
            if (!owner.IsFontItemCommandTargetCurrent(member, item, target)
                || !NodePresenterCombination.Commands(target.Presenters).Contains(command) || !command.CanExecute(target.Presenters, null))
                throw new InvalidOperationException("The native font item command is no longer available.");
            transferred = true;
            StartEditorExtensionCommand(owner, target, command);
        }
        finally
        {
            if (!transferred)
                try { target.Dispose(); }
                catch { packageCommandCleanup.Add(target.Dispose); throw; }
        }
    }
}
