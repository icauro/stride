// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
using Stride.Core.Mathematics;
using Stride.Graphics;

internal static class TextureClearValidation
{
    internal static void Run(GraphicsDevice device, CommandList commands)
    {
        var bits = new UInt4(0x13579bdf, 0x2468ace0, 0x90abcdef, 0x7fc01234);
        var changed = new UInt4(1, 2, 3, 4);
        var count = 0;
        for (var i = 0; i < 256; i++)
        {
            var format = (PixelFormat)i;
            if ((device.Features[format].FormatSupport & FormatSupport.TypedUnorderedAccessView) == 0) continue;
            using var texture = Texture.New2D(device, 13, 7, 3, format, textureData: null, textureFlags: TextureFlags.UnorderedAccess, arraySize: 2);
            commands.ClearReadWrite(texture, bits);
            using var selected = texture.ToTextureView(new TextureViewDescription { Type = ViewType.Single, ArraySlice = 1, MipLevel = 1 });
            commands.ClearReadWrite(selected, changed);
            for (var layer = 0; layer < 2; layer++)
            for (var mip = 0; mip < 3; mip++)
                Check(texture.GetData<byte>(commands, layer, mip), Pixel(format, layer == 1 && mip == 1 ? changed : bits), $"{format} layer {layer} mip {mip}");
            count++;
        }

        using var normalized = Texture.New2D(device, 13, 7, PixelFormat.R8G8B8A8_UNorm, TextureFlags.UnorderedAccess);
        using var before = normalized.ToStaging();
        using var middle = normalized.ToStaging();
        commands.ClearReadWrite(normalized, new UInt4(0x123, 0x245, 0x367, 0x489));
        commands.Copy(normalized, before);
        commands.ClearReadWrite(normalized, new Vector4(-1, .5f, 2, 1));
        commands.Copy(normalized, middle);
        commands.ClearReadWrite(normalized, UInt4.Zero);
        if (before.GetData<uint>(commands).Any(p => p != 0x89674523)
            || middle.GetData<uint>(commands).Any(p => p != 0xffff8000)
            || normalized.GetData<uint>(commands).Any(p => p != 0))
            throw new Exception("Texture clear/copy ordering, integer low bits or normalized conversion failed.");
        using var signed = Texture.New2D(device, 13, 7, PixelFormat.R8G8B8A8_SNorm, TextureFlags.UnorderedAccess);
        commands.ClearReadWrite(signed, new Vector4(-2, 0, 2, -.5f));
        if (signed.GetData<uint>(commands).Any(p => p != 0xc07f0081)) throw new Exception("Texture signed-normalized clear conversion failed.");
        using var half = Texture.New2D(device, 13, 7, PixelFormat.R16G16B16A16_Float, TextureFlags.UnorderedAccess);
        commands.ClearReadWrite(half, new Vector4(-2, .5f, 3.5f, 1));
        if (!half.GetData<ushort>(commands).SequenceEqual(Enumerable.Range(0, 13 * 7 * 4).Select(i => new ushort[] { 0xc000, 0x3800, 0x4300, 0x3c00 }[i % 4])))
            throw new Exception("Texture half-float clear conversion failed.");
        using var integer = Texture.New2D(device, 13, 7, PixelFormat.R32G32B32A32_SInt, TextureFlags.UnorderedAccess);
        commands.ClearReadWrite(integer, new Int4(-7, -2, 3, 4));
        if (!integer.GetData<int>(commands).SequenceEqual(Enumerable.Range(0, 13 * 7 * 4).Select(i => new[] { -7, -2, 3, 4 }[i % 4])))
            throw new Exception("Signed integer texture clear failed.");

        using var line = Texture.New1D(device, 13, PixelFormat.R8G8B8A8_UInt, TextureFlags.UnorderedAccess);
        commands.ClearReadWrite(line, bits); Check(line.GetData<byte>(commands), Pixel(line.Format, bits), "1D");
        using var volume = Texture.New3D(device, 13, 7, 5, 3, PixelFormat.R8G8B8A8_UInt, TextureFlags.UnorderedAccess);
        commands.ClearReadWrite(volume, bits);
        using var volumeMip = volume.ToTextureView(new TextureViewDescription { Type = ViewType.Single, MipLevel = 1 });
        commands.ClearReadWrite(volumeMip, changed);
        for (var mip = 0; mip < 3; mip++) Check(volume.GetData<byte>(commands, 0, mip), Pixel(volume.Format, mip == 1 ? changed : bits), $"3D mip {mip}");
        using var cube = Texture.NewCube(device, 8, 3, PixelFormat.R8G8B8A8_UInt, TextureFlags.UnorderedAccess);
        commands.ClearReadWrite(cube, bits);
        using var face = cube.ToTextureView(new TextureViewDescription { Type = ViewType.Single, ArraySlice = 4, MipLevel = 1 });
        commands.ClearReadWrite(face, changed);
        for (var layer = 0; layer < 6; layer++)
        for (var mip = 0; mip < 3; mip++) Check(cube.GetData<byte>(commands, layer, mip), Pixel(cube.Format, layer == 4 && mip == 1 ? changed : bits), $"Cube face {layer} mip {mip}");

        using var ordinary = Texture.New2D(device, 4, 4, PixelFormat.R8G8B8A8_UNorm);
        Reject<ArgumentException>(() => commands.ClearReadWrite(ordinary, bits));
        Reject<NotSupportedException>(() => commands.ClearReadWrite(integer, Vector4.One));
        ordinary.Dispose(); Reject<ObjectDisposedException>(() => commands.ClearReadWrite(ordinary, bits));
        Console.WriteLine($"PASS: All {count} storage texture formats clear bit-exactly across mip/array views, including float NaN bits; float/half/normalized and signed clears, ordered copies, 1D/3D/cube subresources and invalid calls pass.");
    }

    private static byte[] Pixel(PixelFormat format, UInt4 bits)
    {
        var channelBytes = format switch
        {
            PixelFormat.R8G8B8A8_UNorm or PixelFormat.R8G8B8A8_SNorm or PixelFormat.R8G8B8A8_UInt or PixelFormat.R8G8B8A8_SInt => 1,
            PixelFormat.R16G16B16A16_Float or PixelFormat.R16G16B16A16_UInt or PixelFormat.R16G16B16A16_SInt => 2,
            _ => 4
        };
        var pixel = new byte[format.SizeInBytes];
        for (var i = 0; i < pixel.Length; i++) pixel[i] = (byte)(bits[(uint)(i / channelBytes)] >> (8 * (i % channelBytes)));
        return pixel;
    }
    private static void Check(byte[] actual, byte[] pixel, string name)
    {
        for (var i = 0; i < actual.Length; i++)
            if (actual[i] != pixel[i % pixel.Length]) throw new Exception($"{name}: clear byte {i} was {actual[i]:x2}, expected {pixel[i % pixel.Length]:x2}.");
    }
    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
}
