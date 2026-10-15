namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void RefreshVisualScriptDerivedPresentations(EditorAuthoringState producer, out Action? rollback, bool refreshText = true)
    {
        rollback = null;
        if (!producer.IsPublished || producer.Document is not VisualScriptDocument visualScript) return;
        var views = documents.Entries.Where(entry => entry.State is { IsPublished: true } && entry.Document is ScriptSourceDocument source
            && source.GeneratedByAssetId == producer.AssetId).ToArray();
        if (views.Length == 0) return;
        var owner = GetVisualScriptCompilerOwner(visualScript);
        if (!ReferenceEquals(owner.Producer, producer)) throw new InvalidOperationException("The derived source producer generation changed.");
        var text = refreshText ? visualScript.CaptureCompilation(owner.Item).Result.GeneratedSource
            ?? throw new InvalidDataException("The native visual-script compiler returned no derived presentation.") : null;
        foreach (var entry in views)
        {
            if (entry.State == null || !ReferenceEquals(authoring!.Find(entry.Path), entry.State)
                || !ReferenceEquals(entry.State.Document, entry.Document) || entry.State.IsDisposed
                || entry.Document is not ScriptSourceDocument source || !SamePath(source.Path, owner.Path)
                || source.ProjectPath == null || !SamePath(source.ProjectPath, owner.Project))
                throw new InvalidOperationException("The derived source view has another canonical generation or compiler path.");
        }
        var restorations = new List<Action>();
        rollback = () => { foreach (var restore in restorations.AsEnumerable().Reverse()) restore(); };
        foreach (var entry in views)
        {
            var source = (ScriptSourceDocument)entry.Document;
            restorations.Add(source.ApplyDerivedGeneratedProjection(visualScript.Asset.Id, owner.Project,
                readOnly: owner.Item.Package.IsReadOnly || !source.CanSave, producer, text ?? source.Text));
        }
    }
}
