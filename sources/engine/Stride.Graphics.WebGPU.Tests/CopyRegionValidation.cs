// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
using Stride.Graphics;
using Buffer = Stride.Graphics.Buffer;

internal static class CopyRegionValidation
{
    public static void Run(GraphicsDevice device, CommandList commands)
    {
        using var source = Buffer.New(device, new uint[] { 1, 2, 3, 4, 5, 6 }, BufferFlags.VertexBuffer, GraphicsResourceUsage.Default);
        using var destination = Buffer.New(device, new uint[] { 9, 9, 9, 9, 9, 9 }, BufferFlags.VertexBuffer, GraphicsResourceUsage.Default);
        commands.CopyRegion(source, 0, new ResourceRegion(4, 0, 0, 16, 1, 1), destination, 0, 8);
        if (!destination.GetData<uint>(commands).SequenceEqual(new uint[] { 9, 9, 2, 3, 4, 9 })) throw new Exception("Buffer region copy did not preserve destination boundaries.");

        var pattern = Enumerable.Range(0, 23 * 13).Select(i => (uint)(0xff000000 + i)).ToArray();
        var initial = Enumerable.Repeat(0xffff0000u, 23 * 13).ToArray();
        using var texture = Texture.New2D(device, 23, 13, PixelFormat.R8G8B8A8_UNorm, pattern);
        using var output = Texture.New2D(device, 23, 13, PixelFormat.R8G8B8A8_UNorm, initial);
        using var staging = output.ToStaging();
        commands.Copy(output, staging);
        var region = new ResourceRegion(3, 2, 0, 14, 9, 1);
        commands.CopyRegion(texture, 0, region, output, 0, 7, 4);
        commands.CopyRegion(texture, 0, region, staging, 0, 7, 4);
        var gpuResult = output.GetData<uint>(commands);
        var stagingResult = staging.GetData<uint>(commands);
        for (var y = 0; y < 13; y++)
        for (var x = 0; x < 23; x++)
        {
            var expected = x >= 7 && x < 18 && y >= 4 && y < 11 ? pattern[(y - 2) * 23 + x - 4] : initial[y * 23 + x];
            if (gpuResult[y * 23 + x] != expected || stagingResult[y * 23 + x] != expected)
                throw new Exception($"Texture region copy mismatch at ({x},{y}).");
        }
        try { commands.CopyRegion(texture, 0, region, output, 0, 20); throw new Exception("Out-of-bounds region copy was accepted."); }
        catch (ArgumentOutOfRangeException) { }
        Console.WriteLine("PASS: Buffer and texture region copies preserve offsets and untouched data, including a padded staging destination; out-of-bounds regions are rejected.");
    }
}
