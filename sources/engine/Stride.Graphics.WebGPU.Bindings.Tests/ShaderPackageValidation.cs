// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Stride.Shaders;

internal static class ShaderPackageValidation
{
    internal static void Run()
    {
        // Header-only data tests the envelope codec, not executable shader validity.
        var spirv = MemoryMarshal.AsBytes<uint>(new uint[] { 0x07230203, 0x10000, 0, 1, 0 }).ToArray();
        var variants = new Dictionary<string, string> { ["depth:;storage:"] = "default WGSL", ["depth:3;storage:7=28"] = "specialized WGSL" };
        var package = WebGpuShaderPackage.Create(spirv, variants, "test-converter/1");
        var reordered = WebGpuShaderPackage.Create(spirv, variants.Reverse().ToDictionary(), "test-converter/1");
        if (!package.SequenceEqual(reordered) || !WebGpuShaderPackage.GetSpirV(package).SequenceEqual(spirv) ||
            WebGpuShaderPackage.SelectWgsl(package, "depth:3;storage:7=28") != "specialized WGSL")
            throw new InvalidDataException("WGSL package lost determinism, reflection bytes or exact variant selection.");
        Reject<NotSupportedException>(() => WebGpuShaderPackage.SelectWgsl(package, "depth:4;storage:"));
        Reject<NotSupportedException>(() => WebGpuShaderPackage.SelectWgsl(spirv, "depth:;storage:"));
        Reject<EndOfStreamException>(() => WebGpuShaderPackage.SelectWgsl(package[..^1], "depth:;storage:"));
        var unsupported = package.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(unsupported.AsSpan(4), 2);
        Reject<NotSupportedException>(() => WebGpuShaderPackage.GetSpirV(unsupported));
        var malformed = package.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(malformed.AsSpan(8), int.MaxValue);
        Reject<InvalidDataException>(() => WebGpuShaderPackage.GetSpirV(malformed));
        Console.WriteLine("PASS: WGSL package determinism, exact variant selection, native reflection data, missing variants and malformed/versioned data.");
    }

    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidDataException($"Expected {typeof(T).Name} for invalid WGSL package data.");
    }
}
