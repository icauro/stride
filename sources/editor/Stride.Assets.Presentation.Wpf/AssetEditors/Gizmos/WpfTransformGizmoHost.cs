using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Gizmos;
using Stride.Input;
using Stride.Editor.EditorGame.Game;
using Stride.Assets.Presentation.AssetEditors.GameEditor.Game;
using Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.Game;
using System.Threading.Tasks;

namespace Stride.Assets.Presentation.AssetEditors.Gizmos;

internal sealed class WpfTransformGizmoHost(EditorServiceGame game) : TransformGizmoHost
{
    private IEditorGameCameraService Camera => game.EditorServices.Get<IEditorGameCameraService>();
    public override CameraComponent Component => Camera.Component;
    public override Vector3 Position => Camera.Position;
    public override Vector2 MousePosition => game.Input.MousePosition;
    public override Vector2 ViewportSize => new(game.GraphicsDevice.Presenter.BackBuffer.Width, game.GraphicsDevice.Presenter.BackBuffer.Height);
    public override bool IsMoving => Camera.IsMoving;
    public override bool LeftButtonDown => game.Input.IsMouseButtonDown(MouseButton.Left);
    public override bool ControlDown => game.Input.IsKeyDown(Keys.LeftCtrl) || game.Input.IsKeyDown(Keys.RightCtrl);
    public override bool UseLinearMovementForRotation => Stride.Assets.Presentation.SceneEditor.SceneEditorSettings.UseLinearMovementForRotation.GetValue();
    public override bool CanDuplicateSelection => true;
    public override Task DuplicateSelection() => game.EditorServices.Get<IEditorGameEntitySelectionService>().DuplicateSelection();
}

