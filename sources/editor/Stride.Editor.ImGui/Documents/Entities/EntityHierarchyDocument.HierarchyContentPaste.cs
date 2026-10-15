using Stride.Assets.Entities;
using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    internal bool CanPasteHierarchyContent(string text, IReadOnlyList<(Guid? Parent, string Folder)> targets)
    {
        if (!CanSave || targets.Count == 0 || string.IsNullOrWhiteSpace(text) || text.Length > 16 * 1024 * 1024) return false;
        if (!HierarchyFolderClipboardMetadata.TryRead(text, out var authoredYaml, out var folderPaths)) return false;
        foreach (var target in targets)
        {
            var parent = target.Parent is { } id && Asset.Hierarchy.Parts.TryGetValue(id, out var part) ? part.Entity : null;
            if (target.Parent != null && parent == null || !CanCreateEntity(parent)
                || target.Folder.Length > 0 && !GetHierarchyFolders(target.Parent).Contains(target.Folder, StringComparer.OrdinalIgnoreCase)) return false;
            if (folderPaths.Length > 0 && !CanEditHierarchyFolders(target.Parent)) return false;
            if (!EntityClipboardService.CanPaste(authoredYaml, Asset.GetType(), parent == null
                ? typeof(AssetCompositeHierarchyData<EntityDesign, Stride.Engine.Entity>) : typeof(Stride.Engine.Entity),
                typeof(AssetCompositeHierarchyData<EntityDesign, Stride.Engine.Entity>))) return false;
        }
        return true;
    }
    internal void PasteHierarchyContent(string text, IReadOnlyList<(Guid? Parent, string Folder)> targets)
    {
        if (!CanPasteHierarchyContent(text, targets)) throw new InvalidOperationException("The selected hierarchy content cannot accept the clipboard.");
        HierarchyFolderClipboardMetadata.TryRead(text, out var authoredYaml, out var folderPaths);
        foreach (var target in targets)
        {
            PasteEntitiesWithJournal(authoredYaml, [target.Parent is { } id ? Asset.Hierarchy.Parts[id].Entity : null], target.Folder, allowEmptyFolderPayload: folderPaths.Length > 0);
            if (folderPaths.Length > 0)
            {
                var paths = GetHierarchyFolders(target.Parent).Concat(folderPaths.Select(path => target.Folder.Length == 0 ? path : target.Folder + "/" + path))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                ChangeHierarchyFolders(target.Parent, [], paths, "Paste folders");
            }
        }
    }
}
