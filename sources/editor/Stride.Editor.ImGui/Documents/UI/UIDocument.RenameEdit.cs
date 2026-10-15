using Stride.UI;

namespace Stride.Editor.ImGui;

internal sealed partial class UIDocument
{
    private sealed record NameDraft(Guid Id, string? Original, string Text);
    private NameDraft? elementNameDraft;
    internal Guid? RenamingElement => elementNameDraft?.Id;
    internal string ElementRenameText => elementNameDraft?.Text ?? "";
    public override bool IsDirty => base.IsDirty || elementNameDraft is { } draft
        && !StringComparer.Ordinal.Equals(draft.Original ?? "", draft.Text);
    internal bool CanRenameElement(Guid id) => Find(id) != null
        && Properties(id).FirstOrDefault(field => field.Name == nameof(UIElement.Name)) is { } name && CanEdit(name);
    internal void BeginElementRename(Guid id)
    {
        CommitEdits();
        if (!CanRenameElement(id)) throw new InvalidOperationException("The selected UI element cannot be renamed.");
        elementNameDraft = new(id, Find(id)!.Name, Find(id)!.Name ?? "");
    }
    internal void UpdateElementRenameText(string text)
    {
        if (elementNameDraft is not { } draft) return;
        elementNameDraft = draft with { Text = text };
    }
    internal void CancelElementRename() => elementNameDraft = null;
    internal void CommitElementRename()
    {
        if (elementNameDraft is not { } draft) return;
        if (SessionHistory?.Registry.Find(Asset.Id) is not { IsPublished: true, IsDisposed: false } owner
            || !ReferenceEquals(owner.Document, this) || !CanRenameElement(draft.Id)
            || !StringComparer.Ordinal.Equals(Find(draft.Id)!.Name, draft.Original))
            throw new InvalidOperationException("The UI element changed while its name was being edited.");
        elementNameDraft = null;
        if (StringComparer.Ordinal.Equals(draft.Original ?? "", draft.Text)) return;
        try { Rename(draft.Id, draft.Text); }
        catch { if (!StringComparer.Ordinal.Equals(Find(draft.Id)?.Name, draft.Text)) elementNameDraft = draft; throw; }
    }
}
