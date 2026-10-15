using Stride.Assets;
using Stride.Assets.Materials;
using Stride.Core.Serialization.Contents;
using Stride.Engine;
using Stride.Engine.Design;
using Stride.Games;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Compositing;

namespace Stride.Editor.ImGui;

/// <summary>Runs WPF's model thumbnail composition in an isolated actual engine SceneSystem.</summary>
internal static class ModelAssetThumbnail
{
    private sealed class ThumbnailSceneSystem(Game game) : SceneSystem(game.Services)
    {
        public void InitializeThumbnail() => LoadContent();
    }
    public static (AssetThumbnailImage Thumbnail, byte[] Png) Generate(Game game, ImGuiRenderer renderer, AssetThumbnailSource source)
    {
        var resources = new ThumbnailGpuResources(game, source.RequireCompiledContent());
        Model? model = null; Material? material = null; ThumbnailSceneSystem? system = null;
        var generatedMaterialModel = false;
        Texture? target = null, depth = null;
        var hostColorSpace = game.GraphicsDevice.ColorSpace;
        try
        {
            var content = source.RequireCompiledContent().CreateObjectCache(); resources.Retain(content.UnloadAll);
            if (source.Asset is MaterialAsset)
            {
                material = content.Load<Material>(source.Url, ContentManagerLoaderSettings.StreamingDisabled);
                model = MaterialPreviewGeometry.CreateThumbnailModel(game.Services, material, buffer => resources.Retain(buffer)); generatedMaterialModel = true;
            }
            else model = content.Load<Model>(source.Url, ContentManagerLoaderSettings.StreamingDisabled);
            if (model.Meshes.Count == 0) throw new InvalidDataException("The compiled model has no thumbnail meshes.");
            var entity = new Entity("Thumbnail Entity of model: " + source.Url) { new ModelComponent { Model = model } };
            EntityThumbnailScene.NormalizeEntity(entity, ModelPreviewBounds.Calculate(entity));
            if (generatedMaterialModel) entity.Transform.Rotation = Stride.Core.Mathematics.Quaternion.RotationY(Stride.Core.Mathematics.MathUtil.Pi);
            var camera = new CameraComponent(); var cameraEntity = EntityThumbnailScene.CreateCamera(camera, 1);
            var scene = new Scene(); scene.Entities.Add(entity); scene.Entities.Add(cameraEntity);
            EntityThumbnailScene.SetupLighting(scene, source.RenderingMode == RenderingMode.HDR);
            system = new ThumbnailSceneSystem(game);
            resources.Retain(system);
            system.GraphicsCompositor.Dispose();
            // The reference entity thumbnail compositor disables post effects even in HDR mode.
            system.GraphicsCompositor = GraphicsCompositorHelper.CreateDefault(false,
                generatedMaterialModel ? MaterialPreviewGeometry.EffectName : "StrideEditorForwardShadingEffect", camera,
                Stride.Core.Mathematics.Color.FromBgra(0xFF434343));
            system.SceneInstance = new SceneInstance(game.Services, scene, ExecutionMode.Preview);
            system.InitializeThumbnail();
            var format = source.ColorSpace == ColorSpace.Linear ? PixelFormat.R8G8B8A8_UNorm_SRgb : PixelFormat.R8G8B8A8_UNorm;
            target = Texture.New2D(game.GraphicsDevice, 128, 128, format, TextureFlags.RenderTarget | TextureFlags.ShaderResource);
            resources.Retain(target);
            depth = Texture.New2D(game.GraphicsDevice, 128, 128, PixelFormat.D24_UNorm_S8_UInt, TextureFlags.DepthStencil);
            resources.Retain(depth);
            game.GraphicsDevice.ColorSpace = source.ColorSpace;
            var command = game.GraphicsContext.CommandList;
            command.SetRenderTargetAndViewport(depth, target);
            var time = new GameTime();
            system.Update(time); system.Draw(time);
            system.Update(time); system.Draw(time);
            game.GraphicsDevice.ColorSpace = hostColorSpace;
            var captured = AssetThumbnailImage.Capture(game, renderer, target);
            if (captured.Thumbnail.Owns(target)) { resources.Transfer(target); target = null; }
            return captured;
        }
        finally
        {
            game.GraphicsDevice.ColorSpace = hostColorSpace;
            resources.Retire();
        }
    }
}
