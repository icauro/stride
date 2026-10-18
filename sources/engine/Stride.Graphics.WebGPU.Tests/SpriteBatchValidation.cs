// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
using Stride.Core.Mathematics;
using Stride.Graphics;
using Buffer = Stride.Graphics.Buffer;

internal static class SpriteBatchValidation
{
    public static unsafe void Run(GraphicsDevice device, CommandList commands)
    {
        using var dynamic = Buffer.New(device, new uint[] { 1, 2, 3, 4 }, BufferFlags.VertexBuffer, GraphicsResourceUsage.Dynamic);
        using var before = Buffer.New(device, 16, BufferFlags.None, GraphicsResourceUsage.Staging);
        commands.Copy(dynamic, before);
        var map = commands.MapSubResource(dynamic, 0, MapMode.WriteNoOverwrite, offsetInBytes: 4, lengthInBytes: 8);
        new uint[] { 9, 8 }.AsSpan().CopyTo(new Span<uint>((void*)map.DataBox.DataPointer, 2));
        commands.UnmapSubResource(map);
        if (!before.GetData<uint>(commands).SequenceEqual(new uint[] { 1, 2, 3, 4 }) ||
            !dynamic.GetData<uint>(commands).SequenceEqual(new uint[] { 1, 9, 8, 4 }))
            throw new Exception("Partial WriteNoOverwrite changed data outside its range or reordered an earlier copy.");

        using var target = Texture.New2D(device, 64, 32, PixelFormat.R8G8B8A8_UNorm, TextureFlags.RenderTarget);
        using var first = Texture.New2D(device, 1, 1, PixelFormat.R8G8B8A8_UNorm, new uint[] { uint.MaxValue });
        using var second = Texture.New2D(device, 1, 1, PixelFormat.R8G8B8A8_UNorm, new uint[] { uint.MaxValue });
        using var sprites = new SpriteBatch(device);
        var context = new GraphicsContext(device, commandList: commands);
        foreach (var mode in new[] { SpriteSortMode.Deferred, SpriteSortMode.Immediate })
        {
            commands.SetRenderTargetAndViewport(null, target);
            commands.Clear(target, new Color4(0, 0, 1, 1));
            sprites.Begin(context, mode, blendState: BlendStates.Opaque, samplerState: device.SamplerStates.PointClamp);
            sprites.Draw(first, new RectangleF(0, 0, 32, 32), new Color4(1, 0, 0, 1));
            sprites.Draw(second, new RectangleF(32, 0, 32, 32), new Color4(0, 1, 0, 1));
            sprites.End();
            var pixels = target.GetData<uint>(commands);
            for (var y = 0; y < 32; y++)
                for (var x = 0; x < 64; x++)
                    if (pixels[y * 64 + x] != (x < 32 ? 0xff0000ffu : 0xff00ff00u))
                        throw new Exception($"SpriteBatch {mode} mismatch at {x},{y}: {pixels[y * 64 + x]:x8}.");
        }
        Console.WriteLine("PASS: Partial WriteNoOverwrite preserves range/order; deferred and immediate SpriteBatch render two texture batches with exact pixels.");
    }
}
