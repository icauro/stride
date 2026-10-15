namespace Stride.Editor.ImGui;

internal sealed partial class UIEditorPanel
{
    private readonly Dictionary<string, NativeRotationEditorState> rotationStates = [];
    private NativeRotationEditorState RotationState(string identity, int index)
    {
        // The Euler view belongs to the actual ordered part, not its position in
        // a sorted selection set (selection order can change independently).
        var part = selected.ElementAtOrDefault(index);
        var key = document?.Asset.Id + "/" + part + "/" + identity + "/rotation/" + (part == Guid.Empty ? index : 0);
        if (!rotationStates.TryGetValue(key, out var state)) rotationStates[key] = state = new();
        return state;
    }
}
