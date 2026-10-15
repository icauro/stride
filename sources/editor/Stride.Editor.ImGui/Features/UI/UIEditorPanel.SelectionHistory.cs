namespace Stride.Editor.ImGui;

internal sealed partial class UIEditorPanel
{
    internal bool HasNativeSelectionScope(UIDocument source)
        => ReferenceEquals(document, source) || uiViewSettings.TryGetValue(source, out _);
    internal Guid[] CaptureNativeSelection(UIDocument source)
        => ReferenceEquals(document, source) ? selected.ToArray()
            : uiViewSettings.TryGetValue(source, out var state) ? state.SelectionIds.ToArray() : [];
    internal void RestoreNativeSelection(UIDocument source, IEnumerable<Guid> ids)
    {
        if (!HasNativeSelectionScope(source)) return;
        var actual = ids.Where(id => source.Find(id) != null).Distinct().ToArray();
        if (!ReferenceEquals(document, source))
        {
            var state = uiViewSettings.GetValue(source, _ => new());
            state.SelectionIds = actual; state.WholeAssetProperties = false;
            return;
        }
        CancelCanvasPointer(); StopLayoutDrag(); ClearUITreeKeyboard();
        selected.Clear(); selected.UnionWith(actual); RefreshSelection();
        SaveEditorSelection();
    }
}
