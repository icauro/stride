namespace Stride.Editor.ImGui;

internal sealed record CurvePointNumericTarget(EditorAuthoringState Owner, object Identity,
    Func<object?[]> Read, Func<bool> CanEdit, Action<float> Commit);
