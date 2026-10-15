using System.Runtime.CompilerServices;
using Stride.Core.Mathematics;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class RotationEditors { internal readonly Dictionary<(uint View, string Item), NativeRotationEditorState> Views = []; }
    private readonly ConditionalWeakTable<object, RotationEditors> rotationEditors = new();
    private NativeRotationEditorState RotationEditor(object authoredNode, uint view, string item = "")
    {
        var states = rotationEditors.GetValue(authoredNode, _ => new()).Views;
        var key = (view, item);
        if (!states.TryGetValue(key, out var state)) states.Add(key, state = new());
        return state;
    }
    private float RotationCoordinate(ComponentField field, uint view, int axis)
        => RotationEditor(field.Node, view).Read((Quaternion)(field.Node.Retrieve() ?? Quaternion.Identity))[axis];
    private Quaternion ReplaceRotationCoordinate(ComponentField field, uint view, int axis, float degrees)
        => RotationEditor(field.Node, view).ReplaceAxis((Quaternion)(field.Node.Retrieve() ?? Quaternion.Identity), axis, degrees);
    private NativeRotationEditorState RotationEditor(IHierarchyCollectionValue item, uint view)
    {
        // Stable native ItemIds keep a row's control state across moves/key renames.
        var id = item.IdentityKey;
        return RotationEditor(item.Collection, view, id);
    }
}
