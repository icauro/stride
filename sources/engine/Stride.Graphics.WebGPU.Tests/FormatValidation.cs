// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
using Stride.Graphics;

internal static class FormatValidation
{
    public static void Run(GraphicsDevice device)
    {
        var count = 0;
        var compressedCount = 0;
        for (var i = 0; i < 256; i++)
        {
            var format = (PixelFormat)i;
            var support = device.Features[format].FormatSupport;
            if ((support & FormatSupport.Texture2D) == 0) continue;
            if (format.IsCompressed)
            {
                var forbidden = FormatSupport.RenderTarget | FormatSupport.DepthStencil | FormatSupport.TypedUnorderedAccessView | FormatSupport.MultisampleResolve;
                if ((support & forbidden) != 0 || device.Features[format].MultisampleCountMax != MultisampleCount.None)
                    throw new Exception($"Compressed format {format} advertises unsupported attachment, storage or multisample usage.");
                compressedCount++;
            }
            var flags = (support & FormatSupport.DepthStencil) != 0 ? TextureFlags.DepthStencil : TextureFlags.ShaderResource;
            if ((support & FormatSupport.RenderTarget) != 0) flags |= TextureFlags.RenderTarget;
            if ((support & FormatSupport.TypedUnorderedAccessView) != 0) flags |= TextureFlags.UnorderedAccess;
            using var texture = Texture.New2D(device, 8, 8, format, flags);
            count++;
        }
        var rgba8 = device.Features[PixelFormat.R8G8B8A8_UNorm];
        if ((rgba8.FormatSupport & (FormatSupport.RenderTarget | FormatSupport.ShaderSample | FormatSupport.MultisampleResolve)) !=
            (FormatSupport.RenderTarget | FormatSupport.ShaderSample | FormatSupport.MultisampleResolve) || rgba8.MultisampleCountMax != MultisampleCount.X4)
            throw new Exception("RGBA8 capability reporting omits the implemented render/sample/resolve path.");
        if ((device.Features[PixelFormat.R32G32B32A32_Float].FormatSupport & FormatSupport.Blendable) != 0)
            throw new Exception("Capabilities advertise unimplemented float32 blending.");
        Console.WriteLine($"PASS: All {count} advertised texture formats allocate with their reported usages, including {compressedCount} compressed formats; compressed attachment/storage/MSAA and float32 blending remain disabled.");
    }
}
