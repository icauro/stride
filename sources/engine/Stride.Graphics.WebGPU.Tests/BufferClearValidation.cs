// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
using Stride.Core.Mathematics;
using Stride.Graphics;
using Buffer = Stride.Graphics.Buffer;

internal static class BufferClearValidation
{
    internal static void Run(GraphicsDevice device, CommandList commands)
    {
        using var raw = Buffer.Raw.New(device, Enumerable.Repeat(0x55555555u, 19).ToArray(), BufferFlags.UnorderedAccess);
        using var before = Buffer.New(device, raw.SizeInBytes, BufferFlags.None, GraphicsResourceUsage.Staging);
        using var middle = Buffer.New(device, raw.SizeInBytes, BufferFlags.None, GraphicsResourceUsage.Staging);
        commands.Copy(raw, before);
        commands.ClearReadWrite(raw, new UInt4(0x12345678, 2, 3, 4));
        commands.Copy(raw, middle);
        commands.ClearReadWrite(raw, new UInt4(0, 2, 3, 4));
        if (raw.GetData<uint>(commands).Any(v => v != 0)
            || before.GetData<uint>(commands).Any(v => v != 0x55555555)
            || middle.GetData<uint>(commands).Any(v => v != 0x12345678))
            throw new Exception("Raw clear values or copy/clear ordering were incorrect.");

        using var structured = Buffer.Structured.New<Vector4>(device, 9, unorderedAccess: true);
        commands.ClearReadWrite(structured, new Int4(-7, 2, 3, 4));
        if (structured.GetData<int>(commands).Any(v => v != -7)) throw new Exception("Structured clear did not repeat integer X in every word.");
        using var vectors = Buffer.Typed.New(device, 9, PixelFormat.R32G32B32A32_Float, unorderedAccess: true);
        var vector = new Vector4(.25f, -2, 3.5f, 1);
        commands.ClearReadWrite(vectors, vector);
        if (vectors.GetData<Vector4>(commands).Any(v => v != vector)) throw new Exception("Float4 clear lost distinct channel values.");
        using var triples = Buffer.Typed.New(device, 9, PixelFormat.R32G32B32_UInt, unorderedAccess: true);
        commands.ClearReadWrite(triples, new UInt4(11, 22, 33, 44));
        if (!triples.GetData<uint>(commands).SequenceEqual(Enumerable.Range(0, 27).Select(i => (uint)((i % 3 + 1) * 11))))
            throw new Exception("Three-word typed clear pattern was incorrect.");
        using var packed = Buffer.Typed.New(device, 9, PixelFormat.R8G8B8A8_UNorm, unorderedAccess: true);
        commands.ClearReadWrite(packed, new Vector4(-1, .5f, 2, 1));
        if (packed.GetData<uint>(commands).Any(v => v != 0xffff8000)) throw new Exception("UNorm clear did not clamp and round channels.");
        commands.ClearReadWrite(packed, new UInt4(0x123, 0x245, 0x367, 0x489));
        if (packed.GetData<uint>(commands).Any(v => v != 0x89674523)) throw new Exception("Integer clear did not preserve the low bits of normalized channels.");
        using var halves = Buffer.Typed.New(device, 18, PixelFormat.R16_Float, unorderedAccess: true);
        commands.ClearReadWrite(halves, new Vector4(.5f, 9, 8, 7));
        if (halves.GetData<ushort>(commands).Any(v => v != 0x3800)) throw new Exception("Half-float clear conversion/repetition was incorrect.");
        using var signed = Buffer.Typed.New(device, 9, PixelFormat.R8G8B8A8_SNorm, unorderedAccess: true);
        commands.ClearReadWrite(signed, new Vector4(-2, 0, 2, -.5f));
        if (signed.GetData<uint>(commands).Any(v => v != 0xc07f0081)) throw new Exception("SNorm clear did not preserve signed channel conversion.");

        // Cross the largest one-dimensional dispatch supported on this adapter.
        using var large = Buffer.Typed.New(device, 65535 * 64 + 17, PixelFormat.R32_UInt, unorderedAccess: true);
        commands.ClearReadWrite(large, new UInt4(0x11223344, 0, 0, 0));
        if (large.GetData<uint>(commands).Any(v => v != 0x11223344)) throw new Exception("Two-dimensional clear dispatch missed buffer words.");
        using var plain = Buffer.New(device, 16, BufferFlags.VertexBuffer, GraphicsResourceUsage.Default);
        Reject<ArgumentException>(() => commands.ClearReadWrite(plain, UInt4.Zero));
        Reject<NotSupportedException>(() => commands.ClearReadWrite(raw, Vector4.One));
        Reject<NotSupportedException>(() => commands.ClearReadWrite(triples, Vector4.One));
        plain.Dispose();
        Reject<ObjectDisposedException>(() => commands.ClearReadWrite(plain, UInt4.Zero));
        Console.WriteLine("PASS: Raw/structured/typed UAV buffer clears preserve ordering, distinct channels, packed/normalized/half conversions and large-dispatch tails; invalid clears reject explicitly.");
    }

    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
}
