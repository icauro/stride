using System.Runtime.CompilerServices;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal interface IPropertyDraftOwnerLease : IDisposable
{
    bool ContainsOwner(IEditorDocument owner);
}
internal delegate bool PropertyDraftInput(string control, string hint, ref string text, ImGuiInputTextFlags flags);

internal sealed class PropertyStringEditor
{
    private sealed class Draft
    {
        internal string Text = "";
        internal object?[] Baseline = [];
        internal bool Initialized, Mixed, Edited, Active;
        internal Func<bool>? CanEdit;
        internal Func<object?[]>? Read;
        internal Action<string>? Commit;
        internal IDisposable? Lease;
        internal string? LastValidationError;
        internal Exception? LastValidationException;
    }
    private readonly ConditionalWeakTable<object, Dictionary<uint, Draft>> drafts = new();
    private Draft? pending;
    private Draft? validating;
    internal bool HasPending => pending?.Edited == true;
    internal bool IsPending(object identity, uint controlId)
        => drafts.TryGetValue(identity, out var states) && states.TryGetValue(controlId, out var draft)
            && ReferenceEquals(pending, draft);
    private static bool Equal(object?[] left, object?[] right) => left.Length == right.Length
        && left.Select((value, index) => Equals(value, right[index])).All(equal => equal);
    internal void Draw(object identity, bool mixed, Func<bool> canEdit, Func<object?[]> read,
        Action<string> commit, Func<IDisposable> acquireOwner, Func<string, string?>? step = null,
        Func<object?, string>? format = null, string controlLabel = "##Value", bool refreshAfterCommit = true,
        string? externalText = null, bool externalActive = false, bool externalCommit = false,
        bool cancelOnLostFocus = false, PropertyDraftInput? input = null, bool deferDeactivation = false,
        Func<bool>? cancelInput = null, Action<Exception?>? onCancelled = null)
    {
        var editable = canEdit(); var current = read(); var states = drafts.GetValue(identity, _ => new());
        var id = Gui.GetID(controlLabel);
        bool DrawInput(string hint, ref string text, ImGuiInputTextFlags flags)
            => input != null ? input(controlLabel, hint, ref text, flags) : NativeStringInput.DrawWithHint(controlLabel, hint, ref text, flags);
        if (!states.TryGetValue(id, out var draft)) states[id] = draft = new();
        void Reset()
        {
            draft.Initialized = true; draft.Baseline = current; draft.Mixed = mixed; draft.Edited = false;
            draft.LastValidationError = null;
            draft.LastValidationException = null;
            draft.Text = mixed ? "" : format?.Invoke(current[0]) ?? current[0] as string ?? "";
        }
        if (!draft.Initialized || !draft.Edited && (!Equal(draft.Baseline, current) || draft.Mixed != mixed)) Reset();
        if (editable && (Gui.GetCurrentContext().CurrentItemFlags & (ImGuiItemFlags)ImGuiItemFlagsPrivate.Disabled) != 0)
        {
            var blockedText = draft.Text;
            DrawInput(draft.Mixed ? EditorLocalization.Text("(Different values)") : "", ref blockedText, ImGuiInputTextFlags.EnterReturnsTrue);
            draft.Active = false;
            return;
        }
        if (!editable)
        {
            if (!draft.Edited) { Release(draft); Reset(); }
            var readonlyText = draft.Edited ? draft.Text : mixed ? "" : format?.Invoke(current[0]) ?? current[0] as string ?? "";
            DrawInput(mixed ? EditorLocalization.Text("(Different values)") : "", ref readonlyText, ImGuiInputTextFlags.ReadOnly);
            if (Gui.IsItemActive() && (cancelInput?.Invoke() ?? Gui.IsKeyPressed(ImGuiKey.Escape)))
            { var error = draft.LastValidationException; Release(draft); Reset(); onCancelled?.Invoke(error); }
            return;
        }
        // A real slider gesture contributes to this same retained draft, so Save
        // or a structural command validates its current value before mouse-up.
        var externalEdited = externalActive && externalText != null && externalText != draft.Text;
        var text = externalText != null && (!draft.Edited || externalCommit || externalActive) ? externalText : draft.Text;
        var submitted = DrawInput(draft.Mixed ? EditorLocalization.Text("(Different values)") : "", ref text, ImGuiInputTextFlags.EnterReturnsTrue);
        submitted |= externalCommit;
        var edited = Gui.IsItemEdited() || externalCommit || externalEdited;
        if (step?.Invoke(text) is { } stepped) { text = stepped; edited = true; submitted = true; }
        var textActive = Gui.IsItemActive();
        var active = textActive || externalActive; var deactivated = Gui.IsItemDeactivatedAfterEdit();
        if (Gui.IsItemActivated() || edited || externalActive)
        {
            if (pending != null && !ReferenceEquals(pending, draft)) ValidatePending();
            if (draft.Lease == null)
            {
                draft.Lease = acquireOwner(); draft.CanEdit = canEdit; draft.Read = read; draft.Commit = commit;
            }
            pending = draft;
        }
        var escaped = draft.Active && (cancelInput?.Invoke() ?? Gui.IsKeyPressed(ImGuiKey.Escape));
        draft.Active = textActive;
        if (escaped || cancelOnLostFocus && deactivated && !submitted)
        { var error = draft.LastValidationException; Release(draft); Reset(); onCancelled?.Invoke(error); }
        else
        {
            if (edited) { draft.Text = text; draft.Edited = true; }
            if (draft.Edited && (submitted || deactivated && !deferDeactivation))
            {
                Validate(draft);
                if (refreshAfterCommit) { current = read(); Reset(); }
            }
            else if (!active && !draft.Edited) Release(draft);
        }
    }
    internal void ValidatePending() { if (pending is { } draft) Validate(draft); }
    internal void ValidateExternal(object identity, uint controlId, string text, Func<bool> canEdit, Func<object?[]> read,
        Action<string> commit, Func<IDisposable> acquireOwner)
    {
        var states = drafts.GetValue(identity, _ => new());
        if (!states.TryGetValue(controlId, out var draft)) states[controlId] = draft = new();
        if (pending != null && !ReferenceEquals(pending, draft)) ValidatePending();
        if (!canEdit()) throw new InvalidOperationException("The selected property is no longer editable.");
        if (draft.Lease == null)
        {
            var baseline = read();
            draft.Lease = acquireOwner();
            draft.Baseline = baseline; draft.CanEdit = canEdit; draft.Read = read; draft.Commit = commit;
            draft.Initialized = true; draft.Mixed = false;
        }
        draft.Text = text; draft.Edited = true; pending = draft;
        Validate(draft);
    }
    internal void ValidatePendingForIdentity(object identity)
    {
        if (pending is { } draft && drafts.TryGetValue(identity, out var values)
            && values.Values.Any(value => ReferenceEquals(value, draft))) Validate(draft);
    }
    internal void AbandonPending() { if (pending is { } draft) { draft.Initialized = false; draft.Edited = false; Release(draft); } }
    internal void Abandon(object identity, uint controlId)
    {
        if (!drafts.TryGetValue(identity, out var states) || !states.TryGetValue(controlId, out var draft)) return;
        draft.Initialized = false; draft.Edited = false;
        Release(draft);
    }
    internal void AbandonPending(IEditorDocument owner)
    {
        if (pending?.Lease is IPropertyDraftOwnerLease lease && lease.ContainsOwner(owner)) AbandonPending();
    }
    private void Validate(Draft draft)
    {
        if (ReferenceEquals(validating, draft)) return;
        if (validating != null) throw new InvalidOperationException("A property validation is already in progress.");
        validating = draft;
        try
        {
            if (draft.Edited)
            {
                if (draft.CanEdit?.Invoke() != true || draft.Read == null || !Equal(draft.Baseline, draft.Read()))
                    throw new InvalidOperationException("The selected properties changed before validation.");
                draft.Commit!(draft.Text);
                draft.LastValidationError = null;
                draft.LastValidationException = null;
                draft.Edited = false; draft.Initialized = false;
            }
            Release(draft);
        }
        catch (Exception error) { draft.LastValidationError = error.Message; draft.LastValidationException = error; throw; }
        finally { validating = null; }
    }
    private void Release(Draft draft)
    {
        draft.Lease?.Dispose(); draft.Lease = null;
        draft.CanEdit = null; draft.Read = null; draft.Commit = null; draft.Active = false;
        if (ReferenceEquals(pending, draft)) pending = null;
    }
}
