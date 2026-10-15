using Stride.Assets.Rendering;
using Stride.Animations;
using Stride.Assets.Entities;
using Stride.Assets.Models;
using Stride.UI.Assets;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Design;
using Stride.Engine.Processors;
using Stride.Games;
using Stride.Graphics;
using Stride.Particles.Components;
using Stride.Particles.Rendering;
using Stride.Rendering;
using Stride.Rendering.Compositing;
using Stride.Rendering.UI;
using Stride.SpriteStudio.Assets;
using Stride.SpriteStudio.Runtime;

namespace Stride.Editor.ImGui;

/// <summary>Actual WPF entity-thumbnail camera/light/bounds setup over compiled resources.</summary>
internal static class EntityAssetThumbnail
{
    private sealed class ThumbnailSceneSystem(Game game) : SceneSystem(game.Services)
    {
        public void InitializeThumbnail() => LoadContent();
    }

    public static GraphicsCompositor CreateCompositor(CameraComponent camera, Stride.Core.Assets.PackageTypeScope? scope = null)
    {
        var compositor = GraphicsCompositorHelper.CreateDefault(false, "StrideEditorForwardShadingEffect", camera, Color.FromBgra(0xFF434343));
        var opaque = compositor.RenderStages.First(stage => stage.Name == "Opaque");
        var transparent = compositor.RenderStages.First(stage => stage.Name == "Transparent");
        EditorPackageRenderFeatures.Add(compositor, opaque, transparent, scope);
        return compositor;
    }

    public static (AssetThumbnailImage Thumbnail, byte[] Png) Generate(Game game, ImGuiRenderer renderer,
        SpriteBatch batch, AssetThumbnailSource source)
    {
        _ = source.RequireCompiledContent();
        var resources = new ThumbnailGpuResources(game, source.RequireCompiledContent());
        Model? animationModel = null; AnimationClip? sourceClip = null;
        ThumbnailSceneSystem? system = null;
        Texture? target = null, depth = null, animationIcon = null, combined = null;
        var previousSpace = game.GraphicsDevice.ColorSpace;
        try
        {
            var content = new ThumbnailContentScope(game, source.RequireCompiledContent()); resources.Retain(content);
            var entity = new Entity("Thumbnail Entity: " + source.Url);
            var camera = new CameraComponent();
            var cameraEntity = EntityThumbnailScene.CreateCamera(camera, 1);
            switch (source.Asset)
            {
                case PrefabAsset:
                    var prefab = content.Load<Prefab>(source.Url);
                    foreach (var root in prefab.Entities)
                    {
                        root.Transform.Parent = entity.Transform;
                        SetParticleWarmup(root);
                    }
                    EntityThumbnailScene.NormalizeEntity(entity, Stride.Editor.Engine.EntityExtensions.CalculateBoundSphere(entity));
                    break;
                case UIPageAsset pageAsset:
                    var page = content.Load<UIPage>(source.Url);
                    var resolution = pageAsset.Design.Resolution;
                    var size = resolution / 100f; // WPF UIEditorController.DesignDensity.
                    entity.Add(new UIComponent { Page = page, Resolution = resolution, Size = size, IsFullScreen = false });
                    camera.Projection = CameraProjectionMode.Orthographic;
                    camera.OrthographicSize = Math.Max(size.X, size.Y);
                    break;
                case SpriteStudioModelAsset:
                    var sheet = content.Load<SpriteStudioSheet>(source.Url);
                    entity.Add(new SpriteStudioComponent { Sheet = sheet });
                    EntityThumbnailScene.NormalizeEntity(entity, Stride.Editor.Engine.EntityExtensions.CalculateBoundSphere(entity));
                    entity.Transform.Rotation = Quaternion.Identity;
                    cameraEntity.Transform.Rotation = Quaternion.Identity;
                    break;
                case AnimationAsset animation:
                    var clip = content.Load<AnimationClip>(source.Url);
                    if (source.PreviewModelUrl == null) throw new InvalidOperationException("The animation has no valid preview model.");
                    animationModel = content.Load<Model>(source.PreviewModelUrl);
                    if (animation.Type is DifferenceAnimationAssetType)
                        sourceClip = content.Load<AnimationClip>(source.Url + AnimationAssetCompiler.SrcClipSuffix);
                    entity.Add(new ModelComponent { Model = animationModel });
                    var animator = new AnimationComponent { Animations = { { "preview", sourceClip ?? clip } } };
                    entity.Add(animator); animator.Play("preview");
                    EntityThumbnailScene.NormalizeEntity(entity, Stride.Editor.Engine.EntityExtensions.CalculateBoundSphere(entity));
                    break;
                default:
                    throw new NotSupportedException("The asset has no entity-thumbnail provider.");
            }

            var scene = new Scene(); scene.Entities.Add(entity); scene.Entities.Add(cameraEntity);
            EntityThumbnailScene.SetupLighting(scene, source.RenderingMode == Stride.Assets.RenderingMode.HDR);
            system = new ThumbnailSceneSystem(game);
            resources.Retain(system);
            system.GraphicsCompositor.Dispose(); system.GraphicsCompositor = CreateCompositor(camera, source.TypeScope);
            system.SceneInstance = new SceneInstance(game.Services, scene, ExecutionMode.Thumbnail);
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
            system.Draw(time); // Initialize actual render processors/glyph requests first.
            content.FlushGlyphs();
            system.Update(time); system.Draw(time);

            if (source.Asset is AnimationAsset)
            {
                using var image = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Icons", "thumbnail-animation.png"));
                animationIcon = Texture.Load(game.GraphicsDevice, image, loadAsSrgb: source.ColorSpace == ColorSpace.Linear);
                resources.Retain(animationIcon);
                combined = Texture.New2D(game.GraphicsDevice, 128, 128, format, TextureFlags.RenderTarget | TextureFlags.ShaderResource);
                resources.Retain(combined);
                command.ResourceBarrierTransition(target, BarrierLayout.ShaderResource);
                command.SetRenderTargetAndViewport(null, combined); command.Clear(combined, Color.Zero);
                batch.Begin(game.GraphicsContext);
                try
                {
                    batch.Draw(animationIcon, new RectangleF(0, 0, 128, 128), new Color(255, 255, 255, 32));
                    batch.Draw(target, new RectangleF(0, 0, 128, 128), Color.White);
                }
                finally { batch.End(); }
            }
            game.GraphicsDevice.ColorSpace = previousSpace;
            var captured = AssetThumbnailImage.Capture(game, renderer, combined ?? target);
            if (captured.Thumbnail.Owns(target)) { resources.Transfer(target); target = null; }
            if (combined != null && captured.Thumbnail.Owns(combined)) { resources.Transfer(combined); combined = null; }
            return captured;
        }
        finally
        {
            game.GraphicsDevice.ColorSpace = previousSpace;
            resources.Retire();
        }
    }

    private static void SetParticleWarmup(Entity entity)
    {
        var particles = entity.Get<ParticleSystemComponent>();
        if (particles?.ParticleSystem?.Settings != null && particles.Control != null)
            particles.ParticleSystem.Settings.WarmupTime = particles.Control.ThumbnailWarmupTime;
        foreach (var child in entity.Transform.Children) SetParticleWarmup(child.Entity);
    }
}
