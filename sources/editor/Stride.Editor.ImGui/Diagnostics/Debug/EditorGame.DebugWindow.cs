namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private EditorDebugWindow? ownedDebugWindow;
    private EditorDebugWindow DebugWindow => ownedDebugWindow ??= new(new DebugWindowHost(this));
    private void OpenDebugWindow() => DebugWindow.Visible = true;
    private void DrawDebugWindow() => ownedDebugWindow?.Draw();
    private void ClearDebugWindow() { ownedDebugWindow?.Dispose(); ownedDebugWindow = null; }
    private sealed class DebugWindowHost(EditorGame editor) : IEditorDebugHost
    {
        private readonly List<EditorDebugRoot> failedRoots = [];
        public EditorOutputLog Log => editor.outputLog;
        public EditorSessionHistory? History => editor.sessionHistory;
        public float Scale(float dips) => UiScale(dips);
        public IReadOnlyList<EditorDebugRoot> Graphs()
        {
            DrainFailedGraphAcquisitions();
            var roots = new List<EditorDebugRoot>();
            var owners = editor.authoring?.RetainedStates;
            try
            {
                foreach (var document in editor.documents.AuthoredDocuments)
                {
                    var graph = document switch
                    {
                        AssetMetadataDocument metadata => metadata.AuthoringGraph,
                        EntityHierarchyDocument hierarchy => hierarchy.DebugGraph,
                        ProceduralAssetDocument procedural => procedural.DebugGraph,
                        _ => null,
                    };
                    if (graph == null) continue;
                    var owner = owners?.FirstOrDefault(candidate => ReferenceEquals(candidate.Document, document));
                    if (owner == null) throw new InvalidOperationException("The debug graph has no retained authored owner.");
                    roots.Add(new(System.IO.Path.GetFileNameWithoutExtension(document.Path), graph, owner.AcquireView()));
                }
                return roots;
            }
            catch (Exception acquisitionError)
            {
                failedRoots.AddRange(roots);
                try { DrainFailedGraphAcquisitions(); }
                catch (Exception cleanupError) { throw new AggregateException(acquisitionError, cleanupError); }
                throw;
            }
        }
        public void DrainFailedGraphAcquisitions()
        {
            List<Exception>? errors = null;
            for (var index = failedRoots.Count - 1; index >= 0; index--)
            {
                try { failedRoots[index].Dispose(); failedRoots.RemoveAt(index); }
                catch (Exception error) { (errors ??= []).Add(error); }
            }
            if (errors != null) throw new AggregateException(errors);
        }
        public void DrawSelectableText(string id, string value) => editor.DrawSelectableText(id, value);
    }
}
