using System.Runtime.CompilerServices;
using Hexa.NET.ImGui;
using Stride.Core.Presentation.Quantum.Presenters;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class VirtualStringDraft
    {
        internal string Text = "";
        internal object?[] Sources = [];
        internal bool Initialized, Mixed, Edited, Active;
    }
    private readonly ConditionalWeakTable<VirtualNodePresenter, VirtualStringDraft> virtualStringDrafts = new();
    private void DrawNativeVirtualString(VirtualNodePresenter presenter, bool mixed, bool editable,
        Func<bool> canEdit, Func<object?> read, Action<object?> assign, Action end, Func<object?[]>? readValues)
    {
        object?[] Sources() => readValues?.Invoke() ?? [read()];
        static bool Equal(object?[] left, object?[] right) => left.Length == right.Length
            && left.Select((value, index) => Equals(value, right[index])).All(equal => equal);
        var current = Sources();
        var draft = virtualStringDrafts.GetValue(presenter, _ => new());
        void Reset()
        {
            draft.Initialized = true; draft.Sources = current; draft.Mixed = mixed; draft.Edited = false;
            draft.Text = mixed ? "" : current[0] as string ?? "";
        }
        if (!draft.Initialized || !editable || !draft.Active && (!Equal(draft.Sources, current) || draft.Mixed != mixed)) Reset();
        var text = draft.Text;
        var submitted = NativeStringInput.DrawWithHint("##Value", draft.Mixed ? DifferentPropertyValues : "", ref text,
            ImGuiInputTextFlags.AutoSelectAll | ImGuiInputTextFlags.EnterReturnsTrue
                | (editable ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.ReadOnly));
        var active = Gui.IsItemActive();
        var deactivated = Gui.IsItemDeactivatedAfterEdit();
        var escaped = draft.Active && Gui.IsKeyPressed(ImGuiKey.Escape);
        if (escaped) Reset();
        else
        {
            if (Gui.IsItemEdited()) { draft.Text = text; draft.Edited = true; }
            if (draft.Edited && editable && (submitted || deactivated))
            {
                var expected = draft.Sources; var replacement = draft.Text;
                Execute(() =>
                {
                    if (IsAuthoringPipelineUpdating || !canEdit() || !Equal(expected, Sources()))
                        throw new InvalidOperationException("The selected virtual text properties changed before validation.");
                    assign(replacement); end();
                });
                current = Sources(); Reset();
            }
        }
        draft.Active = active;
    }
}
