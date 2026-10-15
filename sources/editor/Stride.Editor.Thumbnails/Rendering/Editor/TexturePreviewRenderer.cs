// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using System.Collections.Generic;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Graphics;
using StrideEffects;

namespace Stride.Rendering;

/// <summary>Shared engine rendering for texture previews in editor hosts.</summary>
public sealed class TexturePreviewRenderer : IDisposable
{
    private readonly GraphicsDevice device;
    private readonly SpriteBatch batch;
    private readonly DynamicEffectInstance effect;
    private readonly Dictionary<int, Texture> cubeViews = [];
    private SamplerState sampler;
    private Texture? texture;
    private int mipLevel;
    private Texture? checkerboard;
    private SamplerState? checkerSampler;

    public TexturePreviewRenderer(GraphicsDevice device, IServiceRegistry services)
    {
        this.device = device;
        batch = new SpriteBatch(device);
        effect = new DynamicEffectInstance("PreviewTexture");
        effect.Initialize(services);
        sampler = CreateSampler(0);
    }

    /// <summary>The source texture is owned by the caller, not this renderer.</summary>
    public void SetTexture(Texture? source)
    {
        foreach (var view in cubeViews.Values) view.Dispose();
        cubeViews.Clear(); texture = source;
        SetMipLevel(0);
        effect.Parameters.Set(PreviewTextureParameters.Is3D, source?.ViewDimension == TextureDimension.Texture3D);
        if (source?.ViewDimension == TextureDimension.TextureCube)
            for (var face = 0; face < source.ArraySize; face++)
                cubeViews[face] = source.ToTextureView(new TextureViewDescription { ArraySlice = face, Type = ViewType.ArrayBand });
    }

    public void SetMipLevel(int level)
    {
        mipLevel = Math.Clamp(level, 0, Math.Max(0, (texture?.MipLevelCount ?? 1) - 1));
        sampler.Dispose(); sampler = CreateSampler(mipLevel);
    }

    private SamplerState CreateSampler(int level) => SamplerState.New(device,
        new SamplerStateDescription(TextureFilter.Point, TextureAddressMode.Clamp) { MinMipLevel = level, MaxMipLevel = level });

    /// <param name="cubeMode">0..5: right, left, top, bottom, front, back; 6: full cross.</param>
    public Vector2 GetSpriteSize(int cubeMode = 6) => texture == null ? Vector2.Zero :
        new Vector2(texture.Width, texture.Height) * (texture.ViewDimension == TextureDimension.TextureCube && cubeMode == 6 ? new Vector2(4, 3) : Vector2.One);

    public void RenderCheckerboard(GraphicsContext context, Vector2 windowSize, float scale, Vector2 offsets)
        => RenderCheckerboard(context, windowSize, scale, offsets, 1);

    public void RenderCheckerboard(GraphicsContext context, Vector2 windowSize, float scale, Vector2 offsets, float pixelScale)
    {
        if (texture == null) return;
        if (!float.IsFinite(pixelScale) || pixelScale <= 0) throw new ArgumentOutOfRangeException(nameof(pixelScale));
        checkerboard ??= Texture.New2D(device, 2, 2, PixelFormat.R8G8B8A8_UNorm,
            new[] { Color.Gray, Color.LightGray, Color.LightGray, Color.Gray });
        checkerSampler ??= SamplerState.New(device, new SamplerStateDescription(TextureFilter.Point, TextureAddressMode.Wrap));
        var size = new Vector2(texture.Width, texture.Height) * scale;
        var position = windowSize / 2 - size / 2 + offsets * scale;
        batch.Begin(context, SpriteSortMode.Deferred, BlendStates.Opaque, checkerSampler, DepthStencilStates.None, RasterizerStates.CullNone);
        batch.Draw(checkerboard, new RectangleF(position.X, position.Y, size.X, size.Y),
            new RectangleF(0, 0, size.X / (8 * pixelScale), size.Y / (8 * pixelScale)), Color.White, 0, Vector2.Zero);
        batch.End();
    }

    public void Render(GraphicsContext context, Vector2 windowSize, float scale, Vector2 offsets,
        bool premultiplyAlpha, SwizzleMode swizzle, int cubeMode = 6, float depth = 0)
    {
        if (texture == null) return;
        effect.Parameters.Set(Sprite3DBaseKeys.SliceCoordinate, texture.Depth > 1 ? Math.Clamp(depth / (texture.Depth - 1f), 0, 1) : 0);
        var origin = GetSpriteSize(cubeMode) / 2 - offsets;
        var position = windowSize / 2;
        var source = new Rectangle(0, 0, texture.Width, texture.Height);
        batch.Begin(context, SpriteSortMode.Texture, premultiplyAlpha ? BlendStates.AlphaBlend : BlendStates.NonPremultiplied, sampler,
            DepthStencilStates.None, RasterizerStates.CullNone, effect: effect);
        void Draw(Texture view) => batch.Draw(view, position, source, Color.White, 0, origin, scale,
            SpriteEffects.None, ImageOrientation.AsIs, 0, new Color4(0), swizzle);
        if (texture.ViewDimension == TextureDimension.TextureCube)
        {
            if (cubeMode == 6)
            {
                origin.X -= texture.Width; Draw(cubeViews[2]);
                origin.X += texture.Width; origin.Y -= texture.Height; Draw(cubeViews[1]);
                origin.X -= texture.Width; Draw(cubeViews[4]);
                origin.X -= texture.Width; Draw(cubeViews[0]);
                origin.X -= texture.Width; Draw(cubeViews[5]);
                origin.X += 2 * texture.Width; origin.Y -= texture.Height; Draw(cubeViews[3]);
            }
            else Draw(cubeViews[Math.Clamp(cubeMode, 0, 5)]);
        }
        else if (texture.ViewDimension is TextureDimension.Texture2D or TextureDimension.Texture3D) Draw(texture);
        batch.End();
    }

    public void Dispose()
    {
        SetTexture(null); sampler.Dispose(); checkerSampler?.Dispose(); checkerboard?.Dispose(); effect.Dispose(); batch.Dispose();
    }
}
