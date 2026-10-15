using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class HierarchyMoveSelectionPlan(EditorGame editor)
    {
        internal readonly HierarchyMovePresentationSelection Before = editor.SelectionService.CaptureHierarchyMovePresentation();
        internal readonly EntityHierarchyDocument? Scope = editor.PresentedHierarchyDocument;
        internal readonly Dictionary<HierarchyContentItem, HierarchyContentItem> Mapping = [];
        internal void Entity(EntityHierarchyDocument source, Guid original, EntityHierarchyDocument target, Guid replacement)
            => Mapping[HierarchyContentItem.Entity(source, original)] = HierarchyContentItem.Entity(target, replacement);
        internal void Folder(EntityHierarchyDocument source, Guid? parent, string path, EntityHierarchyDocument target, Guid? targetParent, string replacement)
            => Mapping[HierarchyContentItem.Folder(source, parent, path)] = HierarchyContentItem.Folder(target, targetParent, replacement);
        internal void ApplyInitial() => editor.SelectionService.ApplyHierarchyMovePresentation(Before, Mapping, filterUnmoved: true);
        internal Operation Wrap(Operation inner) => new HierarchyMoveSelectionOperation(editor, Scope, inner, Before, Mapping);
        internal void RestoreInitial()
        {
            if (ReferenceEquals(editor.PresentedHierarchyDocument, Scope))
                editor.sessionHistory!.CompensateInitialMutation(() => editor.SelectionService.ApplyHierarchyMovePresentation(Before,
                    new Dictionary<HierarchyContentItem, HierarchyContentItem>(), filterUnmoved: false, validate: false));
        }
    }

    private sealed class HierarchyMoveSelectionOperation(EditorGame editor, EntityHierarchyDocument? scope, Operation inner,
        HierarchyMovePresentationSelection before, IReadOnlyDictionary<HierarchyContentItem, HierarchyContentItem> mapping) : Operation
    {
        private WeakReference<EditorGame>? host = new(editor);
        private WeakReference<EntityHierarchyDocument>? presentation = scope == null ? null : new(scope);
        private Operation? operation = inner;
        private (WeakReference<EntityHierarchyDocument> Owner, Guid? Entity, Guid? FolderOwner, string? Path, bool IsRoot)[]? originalContent
            = before.Content.Select(item => (new WeakReference<EntityHierarchyDocument>(item.Document), item.EntityId, item.FolderOwner, item.FolderPath, item.IsRoot)).ToArray();
        private WeakReference<SceneDocument>[]? originalRoots = before.SceneRoots.Select(root => new WeakReference<SceneDocument>(root)).ToArray();
        private (WeakReference<EntityHierarchyDocument> Owner, Guid? Entity, Guid? FolderOwner, string? Path, bool IsRoot)? originalPrimary
            = before.Primary is { } primary ? (new WeakReference<EntityHierarchyDocument>(primary.Document), primary.EntityId, primary.FolderOwner, primary.FolderPath, primary.IsRoot) : null;
        private WeakReference<SceneDocument>? originalPrimaryScene = before.PrimaryScene == null ? null : new(before.PrimaryScene);
        private HierarchySelectionOrderSnapshot[]? originalOrder = before.Order.Select(EditorSelectionService.DescribeHierarchySelection).ToArray();
        private Dictionary<HierarchyContentItem, HierarchyContentItem>? forward = new(mapping);
        private Dictionary<HierarchyContentItem, HierarchyContentItem>? backward = mapping.ToDictionary(pair => pair.Value, pair => pair.Key);
        private bool innerFrozen;
        public override bool HasEffect => operation?.HasEffect == true;
        protected override void Rollback() => Replay(false);
        protected override void Rollforward() => Replay(true);
        private bool MatchesOriginalContent(HierarchyMovePresentationSelection selected)
        {
            var items = selected.Content;
            if (originalOrder == null || !originalOrder.SequenceEqual(selected.Order.Select(EditorSelectionService.DescribeHierarchySelection))) return false;
            if (originalContent == null || originalContent.Length != items.Length) return false;
            if (originalRoots == null || originalRoots.Length != selected.SceneRoots.Length) return false;
            if (originalPrimary is { } primary)
            {
                if (selected.Primary is not { } currentPrimary || !primary.Owner.TryGetTarget(out var primaryOwner)
                    || !ReferenceEquals(primaryOwner, currentPrimary.Document) || primary.Entity != currentPrimary.EntityId
                    || primary.FolderOwner != currentPrimary.FolderOwner || primary.Path != currentPrimary.FolderPath || primary.IsRoot != currentPrimary.IsRoot) return false;
            }
            else if (selected.Primary != null) return false;
            if (originalPrimaryScene == null ? selected.PrimaryScene != null
                : !originalPrimaryScene.TryGetTarget(out var primaryScene) || !ReferenceEquals(primaryScene, selected.PrimaryScene)) return false;
            for (var index = 0; index < originalRoots.Length; index++)
                if (!originalRoots[index].TryGetTarget(out var root) || !ReferenceEquals(root, selected.SceneRoots[index])) return false;
            for (var index = 0; index < items.Length; index++)
            {
                var previous = originalContent[index]; var item = items[index];
                if (!previous.Owner.TryGetTarget(out var owner) || !ReferenceEquals(item.Document, owner) || item.EntityId != previous.Entity
                    || item.FolderOwner != previous.FolderOwner || item.FolderPath != previous.Path || item.IsRoot != previous.IsRoot) return false;
            }
            return true;
        }
        private void Replay(bool redo)
        {
            var target = host?.TryGetTarget(out var current) == true ? current : null;
            var sameScope = target != null && (presentation == null ? target.PresentedHierarchyDocument == null
                : presentation.TryGetTarget(out var root) && ReferenceEquals(target.PresentedHierarchyDocument, root));
            var selected = sameScope ? target!.SelectionService.CaptureHierarchyMovePresentation() : null;
            if (redo) ((IOperation)operation!).Rollforward(); else ((IOperation)operation!).Rollback();
            if (selected == null || target == null || (presentation == null ? target.PresentedHierarchyDocument != null
                : !presentation.TryGetTarget(out var replayRoot) || !ReferenceEquals(target.PresentedHierarchyDocument, replayRoot))) return;
            try
            {
                target!.SelectionService.ApplyHierarchyMovePresentation(selected, redo ? forward! : backward!,
                    filterUnmoved: redo && MatchesOriginalContent(selected));
            }
            catch (Exception failure)
            {
                try
                {
                    if (redo) ((IOperation)operation!).Rollback(); else ((IOperation)operation!).Rollforward();
                    target!.sessionHistory!.CompensateInitialMutation(() => target.SelectionService.ApplyHierarchyMovePresentation(selected,
                        new Dictionary<HierarchyContentItem, HierarchyContentItem>(), filterUnmoved: false, validate: false));
                }
                catch (Exception recovery) { throw new AggregateException("Hierarchy move replay and selection compensation failed.", failure, recovery); }
                throw;
            }
        }
        protected override void FreezeContent()
        {
            if (!innerFrozen) { ((IOperation)operation!).Freeze(); innerFrozen = true; }
            operation = null; host = null; presentation = null; originalContent = null; originalRoots = null;
            originalPrimary = null; originalPrimaryScene = null; forward = backward = null;
            originalOrder = null;
        }
    }
}
