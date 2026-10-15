namespace Stride.Editor.ImGui;

internal sealed class EditorHierarchyCommandsMutation : IEditorCommandMutation
{
    private readonly EditorSessionHistory history;
    private readonly List<EditorDeferredAssetMutation> mutations = [];
    private bool settled;
    internal EditorHierarchyCommandsMutation(EditorSessionHistory history, IReadOnlyList<EntityHierarchyDocument> owners)
    {
        this.history = history;
        try
        {
            foreach (var owner in owners.Distinct())
            {
                if (!ReferenceEquals(owner.SessionHistory, history) || !ReferenceEquals(history.Registry.Find(owner.Asset.Id)?.Document, owner))
                    throw new InvalidOperationException("The native command's authored owner changed.");
                mutations.Add(owner.BeginExtensionCommandMutation());
            }
            if (mutations.Count == 0) throw new InvalidOperationException("The native command has no authored owners.");
        }
        catch { Dispose(); throw; }
    }
    public void Commit(string title)
    {
        if (settled) throw new InvalidOperationException("The native command is already settled.");
        using var captured = history.CaptureOperationsAtomic(() =>
        {
            foreach (var mutation in mutations) mutation.Commit(title);
        });
        captured.Publish(history, title);
        settled = true;
    }
    public void Rollback()
    {
        if (settled) return;
        // Reverse actual per-owner journals; a failed step stays retained for retry.
        for (var index = mutations.Count - 1; index >= 0; index--) mutations[index].Rollback();
        settled = true;
    }
    public void Dispose()
    {
        if (!settled) Rollback();
        while (mutations.Count != 0) { mutations[0].Dispose(); mutations.RemoveAt(0); }
    }
}
