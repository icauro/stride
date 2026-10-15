// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Stride.Assets.Textures;
using Stride.Core.BuildEngine;
using Stride.Core.Mathematics;
using Stride.Core.Serialization.Contents;
using Stride.Editor.Annotations;
using Stride.Graphics;
using Stride.Rendering;

namespace Stride.Assets.Presentation.Preview
{
    /// <summary>WPF lifecycle adapter for the shared engine texture preview renderer.</summary>
    [AssetPreview<TextureAsset>]
    public class TexturePreview : PreviewFromSpriteBatch<TextureAsset>
    {
        private TexturePreviewRenderer renderer;
        private Texture texture;
        private TextureCubePreviewMode textureCubePreviewMode = TextureCubePreviewMode.Full;
        private float selectedDepth;
        public int TextureWidth => texture?.Width ?? 0;
        public int TextureHeight => texture?.Height ?? 0;
        public int TextureDepth => texture?.Depth ?? 0;
        public Action NotifyTextureLoaded { get; set; }
        public TextureDimension Dimension => texture?.ViewDimension ?? default(TextureDimension);
        public int SliceCount => texture?.ArraySize ?? -1;

        protected override async Task Initialize()
        {
            renderer = new TexturePreviewRenderer(Game.GraphicsDevice, Game.Services);
            await base.Initialize();
        }

        public override IEnumerable<int> GetAvailableMipMaps()
        {
            for (var mip = 0; mip < (texture?.MipLevelCount ?? 1); mip++) yield return mip;
        }
        public override void DisplayMipMap(int level) => renderer.SetMipLevel(level);
        protected override ColorSpace DetermineColorSpace()
        {
            var colorSpace = base.DetermineColorSpace();
            return ((TextureAsset)AssetItem.Asset).Type.IsSRgb(colorSpace) ? ColorSpace.Linear : ColorSpace.Gamma;
        }
        protected SwizzleMode Swizzle() => Asset.Type.Hint == TextureHint.Grayscale ? SwizzleMode.RRR1 :
            Asset.Type.Hint == TextureHint.NormalMap ? SwizzleMode.NormalMap : SwizzleMode.None;
        public override Vector2 SpriteSize => renderer?.GetSpriteSize((int)textureCubePreviewMode) ?? Vector2.Zero;
        public void SetCubePreviewMode(TextureCubePreviewMode mode) => textureCubePreviewMode = mode;
        public void SetDepthToPreview(float depthValue) => selectedDepth = depthValue;

        protected override void LoadContent()
        {
            texture = LoadAsset<Texture>(AssetItem.Location, ContentManagerLoaderSettings.StreamingDisabled);
            MicrothreadLocalDatabases.MountCommonDatabase();
            renderer.SetTexture(texture);
            NotifyTextureLoaded?.Invoke();
            RenderingMode = RenderingMode.LDR;
        }
        protected override void UnloadContent()
        {
            renderer.SetTexture(null);
            if (texture != null) { UnloadAsset(texture); texture = null; }
        }
        protected override void RenderSprite() => renderer.Render(Game.GraphicsContext, WindowSize, SpriteScale,
            SpriteOffsets, Asset.Type.PremultiplyAlpha, Swizzle(), (int)textureCubePreviewMode, selectedDepth);
        public override async Task Dispose()
        {
            await base.Dispose();
            renderer.Dispose();
        }
    }
}
