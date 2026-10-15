using Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.Game;
using Stride.Assets.Presentation.AssetEditors.GameEditor.Game;
using Stride.Core.Mathematics;
using Stride.Graphics.Font;
using Stride.Rendering.Compositing;

namespace Stride.Assets.Presentation.AssetEditors.Gizmos
{
    public partial class SpaceMarker
    {
        internal SpaceMarker(EntityHierarchyEditorGame game) : this(new WpfSpaceMarkerHost(game)) { }

        private sealed class WpfSpaceMarkerHost : SpaceMarkerHost
        {
            private readonly EntityHierarchyEditorGame game;
            internal WpfSpaceMarkerHost(EntityHierarchyEditorGame game) { this.game = game; }
            public override Matrix ViewMatrix => game.EditorServices.Get<IEditorGameCameraService>().ViewMatrix;
            public override GraphicsCompositor Compositor => game.EditorSceneSystem.GraphicsCompositor;
            public override IFontFactory FontFactory => game.Services.GetService<IFontFactory>();
        }
    }
}
