using System.Runtime.CompilerServices;
using Stride.Assets.Presentation.Thumbnails;

// Preserve the original public CLR contracts while their native implementation
// and registration live in the single headless production assembly.
[assembly: TypeForwardedTo(typeof(AnimationThumbnailCompiler))]
[assembly: TypeForwardedTo(typeof(FontThumbnailCompiler))]
[assembly: TypeForwardedTo(typeof(GameSettingsThumbnailCompiler))]
[assembly: TypeForwardedTo(typeof(GraphicsCompositorThumbnailCompiler))]
[assembly: TypeForwardedTo(typeof(MaterialThumbnailCompiler))]
[assembly: TypeForwardedTo(typeof(ModelThumbnailCompiler))]
[assembly: TypeForwardedTo(typeof(PrefabThumbnailCompiler))]
[assembly: TypeForwardedTo(typeof(PrefabModelThumbnailCompiler))]
[assembly: TypeForwardedTo(typeof(PrecompiledFontThumbnailCompiler))]
[assembly: TypeForwardedTo(typeof(ProceduralModelThumbnailCompiler))]
[assembly: TypeForwardedTo(typeof(SceneThumbnailCompiler))]
[assembly: TypeForwardedTo(typeof(ScriptSourceFileThumbnailCompiler))]
[assembly: TypeForwardedTo(typeof(SkeletonThumbnailCompiler))]
[assembly: TypeForwardedTo(typeof(SkyboxThumbnailCompiler))]
[assembly: TypeForwardedTo(typeof(SpriteSheetThumbnailCompiler))]
[assembly: TypeForwardedTo(typeof(TextureThumbnailParameters))]
[assembly: TypeForwardedTo(typeof(Stride.Assets.Presentation.Resources.Thumbnails.StaticThumbnails))]
