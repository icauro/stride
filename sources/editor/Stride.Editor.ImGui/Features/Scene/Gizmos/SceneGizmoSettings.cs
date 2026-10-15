namespace Stride.Editor.ImGui;

internal sealed record SceneTransformSettings(bool TranslationActive, bool RotationActive, bool ScaleActive,
    float TranslationValue, float RotationValue, float ScaleValue, float Size)
{
    internal static SceneTransformSettings Default => new(false, false, false, 1, 22.5f, 1.1f, 1);
    public bool Valid => float.IsFinite(TranslationValue) && TranslationValue > 0
        && float.IsFinite(RotationValue) && RotationValue > 0 && float.IsFinite(ScaleValue) && ScaleValue > 0
        && float.IsFinite(Size) && Size > 0;
}

internal sealed record SceneComponentGizmoSettings(bool Camera, bool Light, bool AudioEmitter, bool AudioListener, float Size, bool FixedSize, string[]? HiddenTypes = null)
{
    internal static SceneComponentGizmoSettings Default => new(true, true, true, true, 1, false,
        [typeof(Stride.Engine.TransformComponent).FullName!, typeof(Stride.Engine.PhysicsComponent).FullName!,
            typeof(Stride.Physics.PhysicsConstraintComponent).FullName!, typeof(Stride.Engine.ModelComponent).FullName!]);
    public bool Valid => float.IsFinite(Size) && Size > 0;
}
