using Stride.UI.Assets;
using Stride.Core.Assets;
using Stride.Core.IO;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void OpenUILibraryDocuments(IReadOnlyList<AssetId> ids)
    {
        if (workspace == null || sessionHistory == null || authoring == null) return;
        var targets = new List<(UIDocument Document, UIHierarchyDragOwner Owner)>();
        try
        {
            foreach (var id in ids.Distinct())
            {
                // Original SourceLibrary resolution omits missing/non-library references.
                var item = workspace.Session.FindAsset(id);
                if (item?.Asset is not UILibraryAsset) continue;
                var path = item.FullPath.ToOSPath();
                var library = GetCanonicalDocument(path, () => new UIDocument(path, sessionHistory, item.Package.IsReadOnly)) as UIDocument
                    ?? throw new InvalidOperationException("The source UI library has no canonical UI document.");
                var state = authoring.Find(id);
                if (state is not { IsPublished: true, IsDisposed: false } || !ReferenceEquals(state.Document, library))
                    throw new InvalidOperationException("The source UI library changed while opening its editor.");
                targets.Add((library, new UIHierarchyDragOwner(state)));
            }
            ValidatePendingPropertyStrings();
            foreach (var target in targets)
            {
                var current = workspace.Session.FindAsset(target.Document.Asset.Id);
                if (!target.Owner.IsCurrent || current?.Asset is not UILibraryAsset
                    || !StringComparer.OrdinalIgnoreCase.Equals(current.FullPath.ToOSPath(), target.Owner.Path))
                    throw new InvalidOperationException("The source UI library changed while opening its editor.");
            }
            foreach (var target in targets)
            {
                if (!target.Owner.IsCurrent) throw new InvalidOperationException("The source UI library changed while opening its editor.");
                var view = documents.Find(target.Document) ?? documents.Add(target.Document);
                ActivateDocument(view);
            }
        }
        finally { foreach (var target in targets) target.Owner.Dispose(); }
    }
}
