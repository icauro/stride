using Stride.Assets.Textures;
using Stride.Core.Mathematics;
using Stride.Graphics;

namespace Stride.Editor.ImGui;

internal sealed record TextureParityRenderResult(string Hint, string PackageColorSpace, string SourceFormat,
    string TargetFormat, int[] ExpectedRgba, int[] ActualRgba, int ChannelTolerance, int DrawCalls);

internal sealed partial class EditorGame
{
    // One owner-thread batch, no project reload or physical UI input. Fixture pixels
    // represent the compiled texture; production texture compilation is a separate contract.
    internal TextureParityRenderResult[] VerifyTextureParityBatch()
    {
        if (!Environment.GetCommandLineArgs().Contains("--ui-test"))
            throw new InvalidOperationException("Texture parity requires an explicit private verification launch.");
        if (renderer == null || workspace == null) throw new InvalidOperationException("Texture parity requires a loaded editor session.");
        var results = new List<TextureParityRenderResult>();
        foreach (var packageSpace in new[] { ColorSpace.Linear, ColorSpace.Gamma })
        foreach (var kind in new[] { "Color", "Grayscale", "NormalMap" })
        {
            ITextureType type = kind switch
            {
                "Grayscale" => new GrayscaleTextureType(),
                "NormalMap" => new NormapMapTextureType(),
                _ => new ColorTextureType { UseSRgbSampling = true, PremultiplyAlpha = false },
            };
            var sourceFormat = type.IsSRgb(packageSpace) ? PixelFormat.R8G8B8A8_UNorm_SRgb : PixelFormat.R8G8B8A8_UNorm;
            var source = Texture.New2D(GraphicsDevice, 2, 2, sourceFormat,
                Enumerable.Repeat(new Color(64, 128, 32, 255), 4).ToArray());
            TextureAssetPreview? preview = null;
            try
            {
                preview = new(this, renderer, source, new TextureAsset { Type = type }, packageSpace);
                preview.Resize(8, 8); preview.SetView(4, Vector2.Zero); preview.Visible = true; preview.Render();
                if (preview.RenderError != null) throw new InvalidOperationException("Texture parity render failed.", preview.RenderError);
                if (preview.DrawCalls <= 0) throw new InvalidOperationException("Texture parity produced no native draw.");
                var pixels = preview.ReadParityPixels();
                if (pixels.Length != 8 * 8 * 4) throw new InvalidDataException("Texture parity readback has incorrect dimensions.");
                var offset = (4 * 8 + 4) * 4;
                var actual = Enumerable.Range(0, 4).Select(channel => (int)pixels[offset + channel]).ToArray();
                // Opaque fixture avoids blend/background ambiguity. Color sRGB decode
                // plus encode must roundtrip. Normal Z follows SpriteBatchShader's
                // documented reconstruction, not a normalized-vector approximation.
                var nx = 64 / 255.0 * 2 - 1; var ny = 128 / 255.0 * 2 - 1;
                var blue = (int)Math.Round((1 - Math.Sqrt(Math.Clamp(nx * nx + ny * ny, 0, 1))) * 127.5 + 127.5);
                var expected = kind switch
                {
                    "Grayscale" => new[] { 64, 64, 64, 255 },
                    "NormalMap" => new[] { 64, 128, blue, 255 },
                    _ => new[] { 64, 128, 32, 255 },
                };
                const int tolerance = 4; // 8-bit conversion and driver quantization only.
                if (actual.Where((value, channel) => Math.Abs(value - expected[channel]) > tolerance).Any())
                    throw new InvalidDataException($"Texture {kind}/{packageSpace} readback [{string.Join(',', actual)}] differs from expected [{string.Join(',', expected)}].");
                var expectedTarget = type.IsSRgb(packageSpace) ? PixelFormat.R8G8B8A8_UNorm_SRgb : PixelFormat.R8G8B8A8_UNorm;
                if (preview.ParityTargetFormat != expectedTarget) throw new InvalidDataException("Texture preview output format does not match its package policy.");
                results.Add(new(kind, packageSpace.ToString(), sourceFormat.ToString(), preview.ParityTargetFormat.ToString(), expected, actual, tolerance, preview.DrawCalls));
            }
            finally
            {
                var resources = new ThumbnailGpuResources(this);
                if (preview != null) resources.Retain(preview);
                else resources.Retain(source);
                resources.Retire();
            }
        }
        return results.ToArray();
    }
}
