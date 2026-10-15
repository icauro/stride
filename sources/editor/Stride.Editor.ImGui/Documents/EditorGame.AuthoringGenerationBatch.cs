namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private int authoredGenerationBatchDepth;
    private bool authoredGenerationViewPending;
    private readonly List<(IEditorDocument Previous, IEditorDocument Next)> authoredGenerationPresentation = [];
    internal IDisposable BeginAuthoredGenerationBatch()
    {
        sessionHistory?.Registry.EnsureOwnerThread(); authoredGenerationBatchDepth++;
        return new AuthoredGenerationBatch(this);
    }
    internal void RefreshAuthoredGenerationBatch()
    {
        sessionHistory?.HierarchyGraphs.RefreshPublished(); sessionHistory?.AssetGraphs.RefreshPublished(); sessionHistory?.UiLibraryGraphs.RefreshPublished();
        foreach (var pair in authoredGenerationPresentation.ToArray())
            if (ReferenceEquals(authoring?.Find(pair.Next.Path)?.Document, pair.Next)) ReplacePresentedHierarchyDocument(pair.Previous, pair.Next);
        authoredGenerationPresentation.Clear();
        if (authoredGenerationViewPending && documents.Active is { } active
            && (active.Document is not EntityHierarchyDocument || !ReferenceEquals(document, active.Document))) ActivateDocument(active);
        authoredGenerationViewPending = false;
    }
    internal void ReplayAuthoredGenerationBatch(bool forward, Action apply, Action restore)
    {
        using var batch = BeginAuthoredGenerationBatch(); var applied = false;
        try { apply(); applied = true; RefreshAuthoredGenerationBatch(); }
        catch (Exception failure)
        {
            try { if (applied) restore(); RefreshAuthoredGenerationBatch(); }
            catch (Exception recovery) { throw new AggregateException("Authored generation batch and restoration failed.", failure, recovery); }
            throw;
        }
    }
    private sealed class AuthoredGenerationBatch(EditorGame game) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            game.sessionHistory?.Registry.EnsureOwnerThread();
            if (game.authoredGenerationBatchDepth <= 0) throw new InvalidOperationException("The authored generation batch has already completed.");
            game.authoredGenerationBatchDepth--; disposed = true;
        }
    }
}
