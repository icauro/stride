namespace Stride.Editor.ImGui;

internal sealed record EditorHistoryEntry(Guid Id, string Name, bool IsDone, bool IsSavePoint);
internal sealed record EditorHistorySnapshot(IReadOnlyList<EditorHistoryEntry> Entries, int Position);
internal interface IEditorHistoryDocument : IEditorDocument
{
    EditorHistorySnapshot History { get; }
}
