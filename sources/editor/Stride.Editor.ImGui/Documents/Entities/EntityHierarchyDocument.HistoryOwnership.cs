namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    public Guid? NextUndoOperationId => transactions.NextUndoTransaction is { Operations.Count: 1 } undo ? undo.Operations[0].Id : null;
    public Guid? NextRedoOperationId => transactions.NextRedoTransaction is { Operations.Count: 1 } redo ? redo.Operations[0].Id : null;
}
