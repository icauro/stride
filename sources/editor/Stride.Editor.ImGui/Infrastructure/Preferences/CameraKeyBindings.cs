using Stride.Input;

namespace Stride.Editor.ImGui;

internal sealed class CameraKeyBindings
{
    public Keys MoveCamForward { get; set; } = Keys.W;
    public Keys MoveCamBackward { get; set; } = Keys.S;
    public Keys MoveCamLeft { get; set; } = Keys.A;
    public Keys MoveCamRight { get; set; } = Keys.D;
    public Keys MoveCamUpward { get; set; } = Keys.E;
    public Keys MoveCamDownward { get; set; } = Keys.Q;
    public Keys IncreaseCamSpeed { get; set; } = Keys.OemPlus;
    public Keys DecreaseCamSpeed { get; set; } = Keys.OemMinus;
    public Keys CenterViewOnSelection { get; set; } = Keys.F;
    public Keys SnapSelectionToGrid { get; set; } = Keys.N;
    public Keys ControlDynamicSnapSelectionToGrid { get; set; } = Keys.LeftShift;
    public Keys TranslationGizmo { get; set; } = Keys.W;
    public Keys RotationGizmo { get; set; } = Keys.E;
    public Keys ScaleGizmo { get; set; } = Keys.R;
    public Keys SwitchGizmo { get; set; } = Keys.Space;
    internal CameraKeyBindings Clone() => (CameraKeyBindings)MemberwiseClone();
    internal bool IsValid => Enum.IsDefined(MoveCamForward) && Enum.IsDefined(MoveCamBackward) && Enum.IsDefined(MoveCamLeft) && Enum.IsDefined(MoveCamRight) && Enum.IsDefined(MoveCamUpward) && Enum.IsDefined(MoveCamDownward) && Enum.IsDefined(IncreaseCamSpeed) && Enum.IsDefined(DecreaseCamSpeed) && Enum.IsDefined(CenterViewOnSelection) && Enum.IsDefined(SnapSelectionToGrid) && Enum.IsDefined(ControlDynamicSnapSelectionToGrid) && Enum.IsDefined(TranslationGizmo) && Enum.IsDefined(RotationGizmo) && Enum.IsDefined(ScaleGizmo) && Enum.IsDefined(SwitchGizmo);
}
