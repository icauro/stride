namespace Stride.Editor.ImGui;

internal sealed record SceneGridSettings(bool Visible, CameraVector Color, float Opacity, int Axes, float Spacing)
{
    public static SceneGridSettings Default { get; } = new(true, new(180f / 255, 180f / 255, 180f / 255), .35f, 2, 1);
    public bool Valid => Color != null && Color.Finite && Color.X >= 0 && Color.Y >= 0 && Color.Z >= 0
        && Color.X <= 1 && Color.Y <= 1 && Color.Z <= 1 && float.IsFinite(Opacity) && Opacity >= 0 && Opacity <= 1
        && Axes >= 0 && (Axes & ~7) == 0 && float.IsFinite(Spacing) && Spacing >= .001f && Spacing <= 1000000;
}
