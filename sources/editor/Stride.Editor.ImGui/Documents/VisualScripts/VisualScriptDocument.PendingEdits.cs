namespace Stride.Editor.ImGui;

internal sealed partial class VisualScriptDocument
{
    private readonly Dictionary<(object Owner, string Member), VisualScriptTextDraft> pendingVisualText = [];
    private bool committingVisualText;
    internal bool HasPendingVisualTextEdits => pendingVisualText.Values.Any(draft => draft.Text != draft.Before);
    public override bool IsDirty => base.IsDirty || pendingVisualText.Values.Any(draft => draft.Text != draft.Before);
    internal VisualScriptTextDraft GetTextDraft(object owner, string member)
    {
        var key = (owner, member); var actual = (string?)ObjectNode(owner)[member].Retrieve() ?? "";
        if (!pendingVisualText.TryGetValue(key, out var draft)) pendingVisualText.Add(key, draft = new(actual));
        if (!draft.Active && draft.Text == draft.Before && draft.Before != actual) draft.Text = draft.Before = actual;
        return draft;
    }
    internal void CommitTextDraft(object owner, string member)
    {
        if (!pendingVisualText.TryGetValue((owner, member), out var draft) || draft.Text == draft.Before) return;
        var actual = (string?)ObjectNode(owner)[member].Retrieve() ?? "";
        if (actual != draft.Before || !CanEditMember(owner, member)) throw new InvalidOperationException("The visual-script field changed while its edit was pending.");
        var wasCommitting = committingVisualText;
        committingVisualText = true;
        try { SetMember(owner, member, draft.Text); draft.Before = draft.Text; }
        finally { committingVisualText = wasCommitting; }
    }
    public override void CommitEdits()
    {
        base.CommitEdits();
        if (committingVisualText) return;
        committingVisualText = true;
        try
        {
            foreach (var key in pendingVisualText.Keys.ToArray()) { CommitTextDraft(key.Owner, key.Member); pendingVisualText[key].Active = false; }
        }
        finally { committingVisualText = false; }
    }
    internal sealed class VisualScriptTextDraft(string original)
    {
        internal string Text = original;
        internal string Before = original;
        internal bool Active;
    }
}
