using Stride.Core.Diagnostics;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private enum ReloadDocumentKind { Scene, Prefab, Procedural, Script, Asset }
    private sealed record ReloadDocument(string Path, ReloadDocumentKind Kind);
    private sealed record ProjectReloadViews(ReloadDocument[] Documents, string? Active, string? Presented);
    private ProjectReloadViews CaptureProjectReloadViews() => new(documents.Entries.Select(entry => new ReloadDocument(entry.Path,
        entry.Document switch { PrefabDocument => ReloadDocumentKind.Prefab, SceneDocument => ReloadDocumentKind.Scene,
            ProceduralAssetDocument => ReloadDocumentKind.Procedural, ScriptSourceDocument => ReloadDocumentKind.Script,
            _ => ReloadDocumentKind.Asset })).ToArray(), documents.Active?.Path, engineViewport?.PresentedDocument?.Path);
    private void RestoreProjectReloadViews(ProjectReloadViews views)
    {
        foreach (var saved in views.Documents)
        {
            try
            {
                if (!File.Exists(saved.Path) || workspace == null
                    || !workspace.AssetItems.Any(item => SamePath(item.FullPath.ToOSPath(), saved.Path))) continue;
                switch (saved.Kind)
                {
                    case ReloadDocumentKind.Scene: OpenScene(saved.Path); break;
                    case ReloadDocumentKind.Prefab: OpenPrefab(saved.Path); break;
                    case ReloadDocumentKind.Procedural: OpenAssetEditor(saved.Path); break;
                    case ReloadDocumentKind.Script: OpenScriptDocument(saved.Path); break;
                    default:
                        var item = workspace!.AssetItems.FirstOrDefault(item => SamePath(item.FullPath.ToOSPath(), saved.Path));
                        if (item == null) continue;
                        GetAssetTagDocument(new AssetEntry(saved.Path, item.UnqualifiedUrl.ToString(), Path.GetExtension(saved.Path),
                            item.Asset.GetType(), item.Id, item.Asset.Tags.ToArray()), false);
                        break;
                }
            }
            catch (Exception failure) { outputLog.Append("Project", LogMessageType.Error, "Reload project", saved.Path + ": " + failure.Message, failure.ToString()); }
        }
        var wanted = views.Documents.Select(saved => saved.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in documents.Entries.Where(entry => !wanted.Contains(entry.Path) && !entry.Document.IsDirty).ToArray()) CloseDocument(entry);
        if (views.Presented != null && documents.Find(views.Presented) is { Document: EntityHierarchyDocument } presented) ActivateDocument(presented);
        if (views.Active != null && documents.Find(views.Active) is { } active) ActivateDocument(active);
        var ordered = views.Documents.Select(saved => documents.Find(saved.Path)).OfType<EditorDocumentEntry>().Distinct().ToList();
        ordered.AddRange(documents.Entries.Where(entry => !ordered.Contains(entry)));
        documents.ApplyOrder(ordered.Select((entry, index) => (entry, index)).ToArray());
    }
}
