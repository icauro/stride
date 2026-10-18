// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
using System.Runtime.InteropServices;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Buffer = Stride.Graphics.Buffer;

internal static class LifetimeValidation
{
    internal static void Run(GraphicsDevice device)
    {
        using var commands = CommandList.New(device);
        var expected = new uint[] { 0x12345678, 0x87654321, 17, 99 };
        using var staging = Buffer.New(device, 16, BufferFlags.None, GraphicsResourceUsage.Staging);
        var source = Buffer.New(device, expected, BufferFlags.VertexBuffer, GraphicsResourceUsage.Default);
        commands.Copy(source, staging);
        source.Dispose();
        var compiled = commands.Close();
        using (var other = GraphicsDevice.New(GraphicsAdapterFactory.DefaultAdapter, DeviceCreationFlags.None, windowHandle: null))
            Reject<InvalidOperationException>(() => other.ExecuteCommandList(compiled), "belongs to another device");
        // The rejected cross-device submit must not consume the original lease.
        device.ExecuteCommandList(compiled);
        commands.Reset();
        if (!staging.GetData<uint>(commands).SequenceEqual(expected)) throw new Exception("Recorded copy did not retain the released source buffer.");
        Console.WriteLine("PASS: Recording retains a released buffer; cross-device submission is rejected without consuming the original command list.");

        var mapping = commands.MapSubResource(staging, 0, MapMode.Read);
        Reject<InvalidOperationException>(() => commands.MapSubResource(staging, 0, MapMode.Read), "already mapped");
        commands.UnmapSubResource(mapping);
        Reject<InvalidOperationException>(() => commands.UnmapSubResource(mapping), "not mapped");
        Reject<ArgumentOutOfRangeException>(() => commands.MapSubResource(staging, 0, MapMode.Read, offsetInBytes: -1));
        MappedResource polled;
        var deadline = Environment.TickCount64 + 10_000;
        while ((polled = commands.MapSubResource(staging, 0, MapMode.Read, doNotWait: true)).DataBox.IsEmpty)
        {
            if (Environment.TickCount64 > deadline) throw new Exception("Nonblocking buffer map did not complete.");
            Thread.Sleep(1);
        }
        if (unchecked((uint)Marshal.ReadInt32(polled.DataBox.DataPointer)) != expected[0]) throw new Exception("Nonblocking buffer map data mismatch.");
        commands.UnmapSubResource(polled);
        using var vertexBuffer = Buffer.New(device, 16, BufferFlags.VertexBuffer, GraphicsResourceUsage.Default);
        Reject<ArgumentOutOfRangeException>(() => vertexBuffer.SetData(commands, new byte[] { 1, 2, 3 }));
        Reject<ArgumentException>(() => commands.Dispatch(vertexBuffer, 0), "indirect argument buffer");
        Console.WriteLine("PASS: Nonblocking buffer readback completes with exact data; duplicate map/unmap, invalid range/alignment and wrong indirect-buffer usage fail explicitly.");

        var mappedStaging = Buffer.New(device, 16, BufferFlags.None, GraphicsResourceUsage.Staging);
        using var gpuSource = Buffer.New(device, expected, BufferFlags.VertexBuffer, GraphicsResourceUsage.Default);
        commands.Copy(gpuSource, mappedStaging);
        var retainedMap = commands.MapSubResource(mappedStaging, 0, MapMode.Read);
        mappedStaging.Dispose();
        if (unchecked((uint)Marshal.ReadInt32(retainedMap.DataBox.DataPointer)) != expected[0]) throw new Exception("Disposing a staging buffer invalidated its outstanding native map reference.");
        commands.UnmapSubResource(retainedMap);

        var dynamic = Buffer.New(device, 16, BufferFlags.VertexBuffer, GraphicsResourceUsage.Dynamic);
        var write = commands.MapSubResource(dynamic, 0, MapMode.WriteDiscard);
        dynamic.Dispose();
        Reject<ObjectDisposedException>(() => commands.UnmapSubResource(write));
        Console.WriteLine("PASS: Read-map lifetime survives resource release until unmap; disposing a mapped upload rejects the write and frees temporary memory.");

        using var target = Texture.New2D(device, 4, 4, PixelFormat.R8G8B8A8_UNorm, TextureFlags.RenderTarget);
        var closed = commands.Close();
        Reject<InvalidOperationException>(() => commands.Clear(target, Color4.White), "Call Reset");
        device.ExecuteCommandList(closed);
        commands.Reset();
        commands.Clear(target, Color4.White);
        if (target.GetData<uint>(commands).Any(p => p != uint.MaxValue)) throw new Exception("Reset after Close did not restore a usable recording.");
        Console.WriteLine("PASS: Recording after Close is rejected; Reset starts a valid encoder and renders correctly.");
    }

    private static void Reject<T>(Action action, string? message = null) where T : Exception
    {
        try { action(); }
        catch (T exception) when (message == null || exception.Message.Contains(message, StringComparison.OrdinalIgnoreCase)) { return; }
        throw new Exception($"Expected {typeof(T).Name}{(message == null ? "" : $" containing '{message}'")}.");
    }
}
