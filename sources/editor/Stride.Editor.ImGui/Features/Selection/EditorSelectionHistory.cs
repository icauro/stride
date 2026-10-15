using Stride.Core.Assets;
using Stride.Core.Transactions;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal sealed record HierarchySelectionSnapshot(AssetId AssetId, string Path, Guid[] Entities, Guid? Primary);
internal sealed record HierarchyContentSelectionSnapshot(AssetId AssetId, Guid? EntityId, Guid? FolderOwner, string? FolderPath, bool IsRoot = false);
internal sealed record UISelectionSnapshot(AssetId AssetId, Guid[] Elements)
{
    internal bool SameSelection(UISelectionSnapshot other) => AssetId == other.AssetId && Elements.AsSpan().SequenceEqual(other.Elements);
}
internal sealed record SpriteSelectionSnapshot(AssetId AssetId, ItemId[] Frames)
{
    internal bool SameSelection(SpriteSelectionSnapshot other) => AssetId == other.AssetId && Frames.AsSpan().SequenceEqual(other.Frames);
}
internal sealed record CompositorListScopeSelection(string Scope, ItemId[] Items);
internal sealed record CompositorSelectionSnapshot(AssetId AssetId, Guid[] Blocks, CompositorListScopeSelection[] Lists,
    Guid? PrimaryBlock, ItemId? PrimaryItem)
{
    internal bool SameSelection(CompositorSelectionSnapshot other) => AssetId == other.AssetId
        && PrimaryBlock == other.PrimaryBlock && PrimaryItem == other.PrimaryItem && Blocks.AsSpan().SequenceEqual(other.Blocks)
        && Lists.Length == other.Lists.Length && Lists.Zip(other.Lists).All(pair => pair.First.Scope == pair.Second.Scope
            && pair.First.Items.AsSpan().SequenceEqual(pair.Second.Items));
}

internal sealed record EditorSelectionSnapshot(string? DocumentPath, Guid[] Entities, Guid? EntityId, AssetId[] Assets, AssetId? Primary)
{
    internal HierarchySelectionSnapshot[] Hierarchies { get; init; } = [];
    internal AssetId? PresentedHierarchy { get; init; }
    internal AssetId? PrimaryHierarchy { get; init; }
    internal HierarchyContentSelectionSnapshot[] Contents { get; init; } = [];
    internal HierarchyContentSelectionSnapshot? PrimaryContent { get; init; }
    internal AssetId[] SceneRoots { get; init; } = [];
    internal AssetId? PrimarySceneRoot { get; init; }
    internal HierarchySelectionOrderSnapshot[] HierarchyContentOrder { get; init; } = [];
    internal string[] AssetContentFolders { get; init; } = [];
    internal string[] AssetContentOrder { get; init; } = [];
    internal string? PrimaryAssetContentFolder { get; init; }
    internal AssetBrowserScopeSnapshot? AssetScope { get; init; }
    internal CompositorSelectionSnapshot[] Compositors { get; init; } = [];
    internal UISelectionSnapshot[] UIElements { get; init; } = [];
    internal SpriteSelectionSnapshot[] Sprites { get; init; } = [];
    internal bool SameSelection(EditorSelectionSnapshot other) => string.Equals(DocumentPath, other.DocumentPath, StringComparison.OrdinalIgnoreCase)
        && EntityId == other.EntityId && Entities.AsSpan().SequenceEqual(other.Entities)
        && Primary == other.Primary && Assets.AsSpan().SequenceEqual(other.Assets)
        && PresentedHierarchy == other.PresentedHierarchy && PrimaryHierarchy == other.PrimaryHierarchy
        && Contents.AsSpan().SequenceEqual(other.Contents)
        && PrimaryContent == other.PrimaryContent
        && SceneRoots.AsSpan().SequenceEqual(other.SceneRoots) && PrimarySceneRoot == other.PrimarySceneRoot
        && HierarchyContentOrder.AsSpan().SequenceEqual(other.HierarchyContentOrder)
        && AssetContentFolders.SequenceEqual(other.AssetContentFolders, StringComparer.OrdinalIgnoreCase)
        && AssetContentOrder.SequenceEqual(other.AssetContentOrder, StringComparer.Ordinal)
        && string.Equals(PrimaryAssetContentFolder, other.PrimaryAssetContentFolder, StringComparison.OrdinalIgnoreCase)
        && (AssetScope == null ? other.AssetScope == null : other.AssetScope != null && AssetScope.SameScope(other.AssetScope))
        && SameHierarchies(other.Hierarchies)
        && Compositors.Length == other.Compositors.Length && Compositors.Zip(other.Compositors).All(pair => pair.First.SameSelection(pair.Second))
        && UIElements.Length == other.UIElements.Length && UIElements.Zip(other.UIElements).All(pair => pair.First.SameSelection(pair.Second))
        && Sprites.Length == other.Sprites.Length && Sprites.Zip(other.Sprites).All(pair => pair.First.SameSelection(pair.Second));
    private bool SameHierarchies(HierarchySelectionSnapshot[] other)
    {
        if (Hierarchies.Length != other.Length) return false;
        for (var index = 0; index < Hierarchies.Length; index++)
            if (Hierarchies[index].AssetId != other[index].AssetId || Hierarchies[index].Primary != other[index].Primary
                || !Hierarchies[index].Entities.AsSpan().SequenceEqual(other[index].Entities)) return false;
        return true;
    }
}

/// <summary>Navigation history resolves immutable authored identities at restore time.</summary>
internal sealed class EditorSelectionHistory
{
    private readonly ITransactionStack stack = TransactionStackFactory.Create(int.MaxValue);
    private EditorSelectionSnapshot? current;
    internal bool CanBack => stack.CanRollback;
    internal bool CanForward => stack.CanRollforward;
    internal void Record(EditorSelectionSnapshot snapshot)
    {
        if (stack.RollInProgress || current?.SameSelection(snapshot) == true) return;
        if (current == null) { current = snapshot; return; }
        using (stack.CreateTransaction()) stack.PushOperation(new SelectionOperation(this, current, snapshot));
        current = snapshot;
    }
    internal void Navigate(int direction, Func<EditorSelectionSnapshot, bool> canResolve, Action<EditorSelectionSnapshot> restore)
    {
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        var initial = current;
        var replayed = 0;
        while (direction < 0 ? stack.CanRollback : stack.CanRollforward)
        {
            if (direction < 0) stack.Rollback(); else stack.Rollforward();
            replayed++;
            if (current == null || initial?.SameSelection(current) == true || !canResolve(current)) continue;
            try { restore(current); }
            catch
            {
                // Pending draft validation must not consume selection history when it rejects
                // navigation. Replay only this independent view stack, never authored edits.
                while (replayed-- > 0)
                    if (direction < 0) stack.Rollforward(); else stack.Rollback();
                throw;
            }
            return;
        }
    }
    internal void Clear() { stack.Clear(); current = null; }
    internal void UnregisterCompositor(AssetId assetId)
    {
        if (current != null) current = WithoutCompositor(current, assetId);
        foreach (var operation in stack.RetrieveAllTransactions().SelectMany(transaction => transaction.Operations).OfType<SelectionOperation>())
            operation.UnregisterCompositor(assetId);
    }
    private static EditorSelectionSnapshot WithoutCompositor(EditorSelectionSnapshot state, AssetId assetId)
        => state with { Compositors = state.Compositors.Where(scope => scope.AssetId != assetId).ToArray() };
    internal void UnregisterUI(AssetId assetId)
    {
        if (current != null) current = WithoutUI(current, assetId);
        foreach (var operation in stack.RetrieveAllTransactions().SelectMany(transaction => transaction.Operations).OfType<SelectionOperation>())
            operation.UnregisterUI(assetId);
    }
    private static EditorSelectionSnapshot WithoutUI(EditorSelectionSnapshot state, AssetId assetId)
        => state with { UIElements = state.UIElements.Where(scope => scope.AssetId != assetId).ToArray() };
    internal void UnregisterSprites(AssetId assetId)
    {
        if (current != null) current = WithoutSprites(current, assetId);
        foreach (var operation in stack.RetrieveAllTransactions().SelectMany(transaction => transaction.Operations).OfType<SelectionOperation>())
            operation.UnregisterSprites(assetId);
    }
    private static EditorSelectionSnapshot WithoutSprites(EditorSelectionSnapshot state, AssetId assetId)
        => state with { Sprites = state.Sprites.Where(scope => scope.AssetId != assetId).ToArray() };
    private sealed class SelectionOperation(EditorSelectionHistory owner, EditorSelectionSnapshot previous, EditorSelectionSnapshot next) : Operation
    {
        private EditorSelectionSnapshot? before = previous, after = next;
        protected override void Rollback() => owner.current = before;
        protected override void Rollforward() => owner.current = after;
        internal void UnregisterCompositor(AssetId assetId)
        {
            if (before != null) before = WithoutCompositor(before, assetId);
            if (after != null) after = WithoutCompositor(after, assetId);
        }
        internal void UnregisterUI(AssetId assetId)
        {
            if (before != null) before = WithoutUI(before, assetId);
            if (after != null) after = WithoutUI(after, assetId);
        }
        internal void UnregisterSprites(AssetId assetId)
        {
            if (before != null) before = WithoutSprites(before, assetId);
            if (after != null) after = WithoutSprites(after, assetId);
        }
        protected override void FreezeContent() { before = after = null; }
    }
}
