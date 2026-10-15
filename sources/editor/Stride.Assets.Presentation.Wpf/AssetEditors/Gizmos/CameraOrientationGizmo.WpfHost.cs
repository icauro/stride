using Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.Game;
using Stride.Assets.Presentation.AssetEditors.GameEditor.Game;
using Stride.Assets.Presentation.SceneEditor;
using Stride.Core.Mathematics;
using Stride.Graphics.Font;
using Stride.Rendering.Compositing;

namespace Stride.Assets.Presentation.AssetEditors.Gizmos
{
    public partial class CameraOrientationGizmo
    {
        internal CameraOrientationGizmo(EditorGameCameraOrientationService service, EntityHierarchyEditorGame game)
            : this(new WpfCameraOrientationHost(service, game)) { }

        private sealed class WpfCameraOrientationHost : CameraOrientationGizmoHost
        {
            private readonly EditorGameCameraOrientationService service;
            private readonly EntityHierarchyEditorGame game;
            internal WpfCameraOrientationHost(EditorGameCameraOrientationService service, EntityHierarchyEditorGame game)
            { this.service = service; this.game = game; }
            public override Matrix ViewMatrix => service.Camera.ViewMatrix;
            public override Vector2 MousePosition => game.Input.MousePosition;
            public override bool DisplayDirectionNames => SceneEditorSettings.DisplayDirectionNames.GetValue();
            public override GraphicsCompositor Compositor => game.EditorSceneSystem.GraphicsCompositor;
            public override IFontFactory FontFactory => game.Services.GetService<IFontFactory>();
        }
    }
}
