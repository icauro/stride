using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class ProjectCreationPanel
{
    private sealed class ProjectFieldDraft
    {
        internal string Text = "", Baseline = "";
        internal bool Active;
        internal bool Dirty => Text != Baseline;
        internal void Reset(string value) { Text = Baseline = value; Active = false; }
    }
    private readonly ProjectFieldDraft nameDraft = new(), locationDraft = new(), solutionNameDraft = new(), solutionLocationDraft = new();
    private string solutionName = "", solutionLocation = "";
    private int projectFieldCancelledFrame = -1;

    private static void BeginProjectFieldRow(string label)
    {
        Gui.TableNextRow();
        Gui.TableSetColumnIndex(0);
        Gui.AlignTextToFramePadding();
        Gui.TextUnformatted(EditorLocalization.Text(label));
        Gui.TableSetColumnIndex(1);
    }

    private void ResetProjectFieldDrafts()
    {
        nameDraft.Reset(name); locationDraft.Reset(location);
        solutionNameDraft.Reset(solutionName); solutionLocationDraft.Reset(solutionLocation);
    }

    private void DrawProjectField(string id, ProjectFieldDraft draft, ref string committed, bool isName,
        ref bool validate, ref bool submit, bool solutionField = false, string? hint = null)
    {
        // Original TextBoxBase updates its explicit binding on validation and
        // UpdateTarget restores it on Escape. Editing never mutates generator inputs.
        if (draft.Baseline != committed || !draft.Active && !draft.Dirty) draft.Reset(committed);
        var cancel = draft.Active && ProjectModalOwnsInput() && Gui.IsKeyPressed(ImGuiKey.Escape, false);
        if (hint == null) NativeStringInput.Draw(id, ref draft.Text);
        else NativeStringInput.DrawWithHint(id, hint, ref draft.Text);
        var active = Gui.IsItemActive();
        var accept = SubmitProjectField();
        if (cancel)
        {
            ImGuiP.SetKeyOwner(ImGuiKey.Escape, Gui.GetItemID(), (ImGuiInputFlags)ImGuiInputFlagsPrivate.LockThisFrame);
            draft.Reset(committed); projectFieldCancelledFrame = Gui.GetFrameCount();
            return;
        }
        if (accept || Gui.IsItemDeactivatedAfterEdit())
        {
            if (CommitProjectField(draft, ref committed, isName, solutionField)) validate = true;
            submit |= accept;
        }
        draft.Active = active;
    }

    private bool CommitProjectField(ProjectFieldDraft draft, ref string committed, bool isName, bool solutionField = false)
    {
        if (!draft.Dirty) return false;
        var previous = committed;
        committed = draft.Text;
        draft.Reset(committed);
        if (!solutionField)
        {
            if (isName) nameWasEdited = !string.IsNullOrEmpty(committed) && previous != committed;
            else UpdateDefaultName();
        }
        return true;
    }

    private bool CommitProjectFieldDrafts(bool validate)
    {
        if (projectValidationMessage != null || projectValidationAcceptedFrame == Gui.GetFrameCount()) return false;
        var changed = CommitProjectField(nameDraft, ref name, true);
        changed |= CommitProjectField(locationDraft, ref location, false);
        if (selectionSource != null)
        {
            changed |= CommitProjectField(solutionNameDraft, ref solutionName, false, true);
            changed |= CommitProjectField(solutionLocationDraft, ref solutionLocation, false, true);
        }
        return !validate || !changed || selected == null || ValidateProjectDestination();
    }
}
