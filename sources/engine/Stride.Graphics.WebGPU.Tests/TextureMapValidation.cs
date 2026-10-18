// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
using Stride.Graphics;

internal static class TextureMapValidation
{
    internal static unsafe void Run(GraphicsDevice device, CommandList commands)
    {
        using var texture = Texture.New2D(device, 13, 7, PixelFormat.R8G8B8A8_UNorm, usage: GraphicsResourceUsage.Dynamic);
        var original = Enumerable.Range(0, 13 * 7).Select(i => (uint)(0xff000000 | i)).ToArray();
        var updated = Enumerable.Range(0, 13 * 7).Select(i => (uint)(0xff001000 | i)).ToArray();
        texture.SetData(commands, original);
        using var before = texture.ToStaging();
        using var after = texture.ToStaging();
        var mapped = commands.MapSubResource(texture, 0, MapMode.WriteDiscard);
        if (mapped.DataBox.RowPitch < 13 * 4 || mapped.DataBox.RowPitch % 256 != 0 || mapped.DataBox.SlicePitch < mapped.DataBox.RowPitch * 7)
            throw new Exception("Dynamic texture map returned invalid padded pitches.");
        for (var y = 0; y < 7; y++)
            updated.AsSpan(y * 13, 13).CopyTo(new Span<uint>((byte*)mapped.DataBox.DataPointer + y * mapped.DataBox.RowPitch, 13));
        Reject<InvalidOperationException>(() => commands.MapSubResource(texture, 0, MapMode.WriteDiscard));
        commands.Copy(texture, before); // The pending CPU upload must not affect this copy.
        commands.UnmapSubResource(mapped);
        commands.Copy(texture, after);
        var polled = new uint[updated.Length];
        var pollDeadline = Environment.TickCount64 + 10_000;
        while (!after.GetData(commands, polled, doNotWait: true))
        {
            if (Environment.TickCount64 > pollDeadline) throw new Exception("Nonblocking texture readback did not complete.");
            Thread.Sleep(1);
        }
        if (!polled.SequenceEqual(updated)) throw new Exception("Nonblocking texture readback data mismatch.");
        // Reusing a staging target must cancel an outstanding map before copying.
        commands.Copy(texture, after);
        _ = after.GetData(commands, polled, doNotWait: true);
        commands.Copy(texture, after);
        if (!before.GetData<uint>(commands).SequenceEqual(original) || !after.GetData<uint>(commands).SequenceEqual(updated))
            throw new Exception("Dynamic texture mapping reordered commands or corrupted row padding.");
        Reject<InvalidOperationException>(() => commands.UnmapSubResource(mapped));

        using var array = Texture.New2D(device, 13, 7, 3, PixelFormat.R8G8B8A8_UNorm, textureData: null, arraySize: 2, usage: GraphicsResourceUsage.Dynamic);
        var expected = new uint[6][];
        for (var layer = 0; layer < 2; layer++)
        for (var mip = 0; mip < 3; mip++)
        {
            var index = layer * 3 + mip;
            expected[index] = Enumerable.Range(0, Math.Max(1, 13 >> mip) * Math.Max(1, 7 >> mip)).Select(i => (uint)(index * 1000 + i)).ToArray();
            array.SetData(commands, expected[index], layer, mip);
        }
        for (var layer = 0; layer < 2; layer++)
        for (var mip = 0; mip < 3; mip++)
            if (!array.GetData<uint>(commands, layer, mip).SequenceEqual(expected[layer * 3 + mip]))
                throw new Exception($"Dynamic texture array/mip upload failed at {layer}/{mip}.");

        using var volume = Texture.New3D(device, 13, 7, 5, 3, PixelFormat.R8G8B8A8_UNorm, usage: GraphicsResourceUsage.Dynamic);
        for (var mip = 0; mip < 3; mip++)
        {
            var data = Enumerable.Range(0, Math.Max(1, 13 >> mip) * Math.Max(1, 7 >> mip) * Math.Max(1, 5 >> mip)).Select(i => (uint)(mip * 1000 + i)).ToArray();
            volume.SetData(commands, data, mipLevel: mip);
            if (!volume.GetData<uint>(commands, mipLevel: mip).SequenceEqual(data)) throw new Exception($"Dynamic volume slice pitch failed at mip {mip}.");
        }
        using var line = Texture.New1D(device, 13, PixelFormat.R8G8B8A8_UNorm, usage: GraphicsResourceUsage.Dynamic);
        line.SetData(commands, updated[..13]);
        if (!line.GetData<uint>(commands).SequenceEqual(updated[..13])) throw new Exception("Dynamic 1D upload failed.");
        using var cube = Texture.NewCube(device, 8, PixelFormat.R8G8B8A8_UNorm, usage: GraphicsResourceUsage.Dynamic);
        for (var face = 0; face < 6; face++) cube.SetData(commands, Enumerable.Repeat((uint)(face + 1), 64).ToArray(), face);
        for (var face = 0; face < 6; face++)
            if (cube.GetData<uint>(commands, face).Any(v => v != face + 1)) throw new Exception("Dynamic cube face upload failed.");

        Reject<ArgumentOutOfRangeException>(() => commands.MapSubResource(array, -1, MapMode.WriteDiscard));
        Reject<ArgumentOutOfRangeException>(() => commands.MapSubResource(array, 6, MapMode.WriteDiscard));
        Reject<ArgumentException>(() => commands.MapSubResource(texture, 0, MapMode.WriteDiscard, offsetInBytes: 4));
        Reject<NotSupportedException>(() => commands.MapSubResource(texture, 0, MapMode.WriteNoOverwrite));
        var released = Texture.New2D(device, 4, 4, PixelFormat.R8G8B8A8_UNorm, usage: GraphicsResourceUsage.Dynamic);
        var releasedMap = commands.MapSubResource(released, 0, MapMode.WriteDiscard);
        released.Dispose();
        Reject<ObjectDisposedException>(() => commands.UnmapSubResource(releasedMap));
        using (var abandoned = CommandList.New(device)) abandoned.MapSubResource(texture, 0, MapMode.WriteDiscard);
        var retry = commands.MapSubResource(texture, 0, MapMode.WriteDiscard); commands.UnmapSubResource(retry);
        Console.WriteLine("PASS: Dynamic texture WriteDiscard/public SetData preserve pitches and command ordering for 1D/2D arrays/mips/3D/cube; duplicate, invalid and disposed maps reject and abandoned upload mappings clean up.");
    }

    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
}
