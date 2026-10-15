using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class AssetCreationPanel
{
    private bool acceptingTemplate, focusTemplateParameters, focusTemplateSearch;
    private Choice? scrollTemplateChoice;

    private bool CanAcceptTemplate => create != null && selected != null
        && (selected.Template?.RequireName == false || !string.IsNullOrWhiteSpace(name)) && !acceptingTemplate;
    private static bool TemplateModalOwnsInput() => !Gui.GetIO().AppFocusLost
        && Gui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)
        && !Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId)
        && !Gui.GetIO().KeyCtrl && !Gui.GetIO().KeyAlt && !Gui.GetIO().KeySuper && !Gui.GetIO().KeyShift;
    private static void OwnTemplateKey(ImGuiKey key, uint id)
        => ImGuiP.SetKeyOwner(key, id, (ImGuiInputFlags)ImGuiInputFlagsPrivate.LockThisFrame);
    private void AcceptTemplate(Action<Action> execute)
    {
        if (!CanAcceptTemplate) return;
        execute(() =>
        {
            if (!CanAcceptTemplate) return;
            acceptingTemplate = true;
            try
            {
                create!(selected!, name, selected!.Template?.ImportSource == true, reference, sourceNamespace);
                Gui.CloseCurrentPopup(); create = null;
                if (resolveWithoutSource == null) ReleaseAdapters();
            }
            finally { acceptingTemplate = false; }
        });
    }
    private void ValidateTemplateChoice(Choice choice, Action<Action> execute)
    {
        var accept = false;
        execute(() =>
        {
            if (create == null || !TemplateModalOwnsInput()) return;
            SelectTemplateChoice(choice);
            // The port hosts the native specialized parameter preparation in this dialog.
            if (AssetCreationReferenceKind(choice.Template?.Id) != null)
            { BrowseTemplateReference(choice); return; }
            if (choice.AssetType == typeof(Stride.Assets.Scripts.ScriptSourceFileAsset)
                || choice.Adapter?.Adapter.DrawParameters != null)
            { focusTemplateParameters = true; return; }
            accept = true;
        });
        if (accept) AcceptTemplate(execute);
    }
    private bool ReadTemplateSearchNavigation(bool ownsSearch, uint searchId, Action<Action> execute)
    {
        if (!ownsSearch || !TemplateModalOwnsInput()) return false;
        var visible = VisibleTemplateChoices();
        var index = Array.IndexOf(visible, selected);
        var next = index;
        var page = Math.Max(1, (int)(240f / 56));
        ReadOnlySpan<ImGuiKey> keys = [ImGuiKey.UpArrow, ImGuiKey.DownArrow, ImGuiKey.PageUp, ImGuiKey.PageDown, ImGuiKey.Home, ImGuiKey.End];
        foreach (var key in keys)
        {
            if (!Gui.IsKeyPressed(key)) continue;
            OwnTemplateKey(key, searchId);
            next = key switch
            {
                ImGuiKey.UpArrow => Math.Max(0, index - 1),
                ImGuiKey.DownArrow => Math.Min(visible.Length - 1, index + 1),
                ImGuiKey.PageUp => Math.Max(0, index - page),
                ImGuiKey.Home => 0, ImGuiKey.End => visible.Length - 1,
                _ => Math.Min(visible.Length - 1, index + page),
            };
            if (next >= 0 && next < visible.Length)
            { var choice = visible[next]; execute(() => SelectTemplateChoice(choice)); scrollTemplateChoice = choice; }
            break;
        }
        if (Gui.IsKeyPressed(ImGuiKey.Enter, false) || Gui.IsKeyPressed(ImGuiKey.KeypadEnter, false))
        {
            OwnTemplateKey(ImGuiKey.Enter, searchId); OwnTemplateKey(ImGuiKey.KeypadEnter, searchId);
            if (selected != null && visible.Contains(selected)) ValidateTemplateChoice(selected, execute);
            return true;
        }
        return false;
    }
}
