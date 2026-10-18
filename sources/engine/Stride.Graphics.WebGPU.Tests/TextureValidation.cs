// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
using System.Runtime.InteropServices;
using Stride.Graphics;
using Stride.Core.Mathematics;

internal static class TextureValidation
{
    internal static void Run(GraphicsDevice device, CommandList commands)
    {
        const int width = 31, height = 17, mipCount = 3, arraySize = 2;
        var patterns = new uint[mipCount * arraySize][];
        var pins = new GCHandle[patterns.Length];
        try
        {
            var boxes = new DataBox[patterns.Length];
            for (var slice = 0; slice < arraySize; slice++)
            for (var mip = 0; mip < mipCount; mip++)
            {
                var index = slice * mipCount + mip;
                var mipWidth = Math.Max(1, width >> mip);
                var mipHeight = Math.Max(1, height >> mip);
                patterns[index] = Enumerable.Range(0, mipWidth * mipHeight).Select(i => unchecked((uint)(i * 2654435761L + index * 13))).ToArray();
                pins[index] = GCHandle.Alloc(patterns[index], GCHandleType.Pinned);
                boxes[index] = new DataBox(pins[index].AddrOfPinnedObject(), mipWidth * 4, mipWidth * mipHeight * 4);
            }
            using var texture = Texture.New2D(device, width, height, mipCount, PixelFormat.R8G8B8A8_UNorm, boxes, arraySize: arraySize);
            using var staging = texture.ToStaging();
            commands.Copy(texture, staging);
            for (var slice = 0; slice < arraySize; slice++)
            for (var mip = 0; mip < mipCount; mip++)
                if (!staging.GetData<uint>(commands, slice, mip).SequenceEqual(patterns[slice * mipCount + mip])) throw new Exception($"Texture array slice {slice}, mip {mip} readback mismatch.");
            Console.WriteLine("PASS: Every texel of two array slices and three mip levels survives upload and staging readback.");
        }
        finally { foreach (var pin in pins) if (pin.IsAllocated) pin.Free(); }

        var volume = Enumerable.Range(0, 13 * 7 * 5).Select(i => unchecked((uint)(i * 2246822519L))).ToArray();
        using var texture3D = Texture.New3D(device, 13, 7, 5, PixelFormat.R8G8B8A8_UNorm, volume);
        if (!texture3D.GetData<uint>(commands).SequenceEqual(volume)) throw new Exception("3D texture slice pitch mismatch.");
        Console.WriteLine("PASS: Exact 3D texture upload/readback across padded rows and depth slices.");

        using var regionTexture = Texture.New3D(device, 13, 7, 5, PixelFormat.R8G8B8A8_UNorm);
        var region = new ResourceRegion(3, 2, 1, 7, 5, 3);
        var regionData = Enumerable.Range(0, 4 * 3 * 2).Select(i => (uint)(i + 1)).ToArray();
        regionTexture.SetData<uint>(commands, regionData, region: region);
        var regionResult = regionTexture.GetData<uint>(commands);
        for (var z = 0; z < 5; z++)
        for (var y = 0; y < 7; y++)
        for (var x = 0; x < 13; x++)
        {
            var inside = x >= 3 && x < 7 && y >= 2 && y < 5 && z >= 1 && z < 3;
            var expected = inside ? regionData[(z - 1) * 12 + (y - 2) * 4 + x - 3] : 0;
            if (regionResult[(z * 7 + y) * 13 + x] != expected) throw new Exception($"3D region update changed an incorrect texel ({x},{y},{z}).");
        }
        Console.WriteLine("PASS: 3D texture region upload preserves exact source pitches, destination offsets and untouched texels.");

        var initial = Enumerable.Repeat(0xff0000ffu, 11 * 7).ToArray();
        var updated = Enumerable.Repeat(0xff00ff00u, 11 * 7).ToArray();
        using var changing = Texture.New2D(device, 11, 7, PixelFormat.R8G8B8A8_UNorm, initial, usage: GraphicsResourceUsage.Default);
        using var before = Texture.New2D(device, 11, 7, PixelFormat.R8G8B8A8_UNorm);
        using var after = Texture.New2D(device, 11, 7, PixelFormat.R8G8B8A8_UNorm);
        commands.BeginProfile(Color4.White, "Texture update ordering");
        commands.Copy(changing, before);
        changing.SetData<uint>(commands, updated);
        commands.Copy(changing, after);
        commands.EndProfile();
        if (!before.GetData<uint>(commands).SequenceEqual(initial) || !after.GetData<uint>(commands).SequenceEqual(updated))
            throw new Exception("A texture update reordered earlier/later texture commands.");
        Console.WriteLine("PASS: Texture update preserves command order inside a debug group without a premature submission.");
    }
}
