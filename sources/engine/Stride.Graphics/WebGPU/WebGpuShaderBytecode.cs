// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
#if STRIDE_GRAPHICS_API_WEBGPU
#nullable enable annotations
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Stride.Shaders;

namespace Stride.Graphics;

/// <summary>Prepares Stride SPIR-V for native validation, including resource-dependent depth image types.</summary>
internal static partial class WebGpuShaderBytecode
{
    internal static HashSet<uint> NullableFloat2DTextures(ReadOnlySpan<byte> source)
    {
        var words = MemoryMarshal.Cast<byte, uint>(Prepare(source));
        var bindings = new Dictionary<uint, uint>();
        var pointers = new Dictionary<uint, uint>();
        var images = new Dictionary<uint, uint>();
        var floats = new HashSet<uint>();
        var variables = new List<(uint Id, uint Type)>();
        for (var i = 5; i < words.Length; i += (int)(words[i] >> 16))
        {
            var count = (int)(words[i] >> 16);
            switch (words[i] & 0xffff)
            {
                case 71 when count == 4 && words[i + 2] == 33: bindings[words[i + 1]] = words[i + 3]; break;
                case 22 when count == 3 && words[i + 2] == 32: floats.Add(words[i + 1]); break;
                // Only ordinary, single-sample, non-array float2D reads. Storage,
                // depth, integer and other dimensions must keep explicit bindings.
                case 25 when count >= 9 && words[i + 3] == 1 && words[i + 4] != 1 && words[i + 5] == 0 && words[i + 6] == 0 && words[i + 7] == 1:
                    images[words[i + 1]] = words[i + 2]; break;
                case 32 when count == 4: pointers[words[i + 1]] = words[i + 3]; break;
                case 59 when count >= 4 && words[i + 3] == 0: variables.Add((words[i + 2], words[i + 1])); break;
            }
        }
        var result = new HashSet<uint>();
        foreach (var variable in variables)
            if (bindings.TryGetValue(variable.Id, out var binding) && pointers.TryGetValue(variable.Type, out var image)
                && images.TryGetValue(image, out var scalar) && floats.Contains(scalar)) result.Add(binding);
        return result;
    }

    internal static HashSet<uint> ResourceBindings(ReadOnlySpan<byte> source)
    {
        var words = MemoryMarshal.Cast<byte, uint>(Prepare(source));
        var bindings = new Dictionary<uint, uint>();
        var variables = new HashSet<uint>();
        for (var i = 5; i < words.Length; i += (int)(words[i] >> 16))
        {
            var count = (int)(words[i] >> 16);
            if ((words[i] & 0xffff) == 71 && count == 4 && words[i + 2] == 33) bindings[words[i + 1]] = words[i + 3];
            if ((words[i] & 0xffff) == 59 && count >= 4) variables.Add(words[i + 2]);
        }
        var result = new HashSet<uint>();
        foreach (var (id, binding) in bindings) if (variables.Contains(id)) result.Add(binding);
        return result;
    }

    internal static Dictionary<uint, uint> StorageBufferStrides(ReadOnlySpan<byte> source)
    {
        // Prepare validates the instruction boundaries before this reflection walk.
        var prepared = Prepare(source);
        var words = MemoryMarshal.Cast<byte, uint>(prepared);
        var strides = new Dictionary<uint, uint>();
        var bindings = new Dictionary<uint, uint>();
        var pointers = new Dictionary<uint, uint>();
        var arraysByBlock = new Dictionary<uint, uint>();
        var variables = new List<(uint Id, uint Type)>();
        for (var i = 5; i < words.Length;)
        {
            var count = (int)(words[i] >> 16);
            switch (words[i] & 0xffff)
            {
                case 71 when count >= 4: // OpDecorate
                    if (words[i + 2] == 6) strides[words[i + 1]] = words[i + 3]; // ArrayStride
                    if (words[i + 2] == 33) bindings[words[i + 1]] = words[i + 3]; // Binding
                    if (words[i + 2] == 34 && words[i + 3] != 0) // DescriptorSet
                        throw new NotSupportedException("WebGPU currently expects Stride's unified descriptor set zero.");
                    break;
                case 30 when count >= 3: // OpTypeStruct; buffer runtime array is the last member
                    arraysByBlock[words[i + 1]] = words[i + count - 1];
                    break;
                case 32 when count == 4: // OpTypePointer
                    pointers[words[i + 1]] = words[i + 3];
                    break;
                case 59 when count >= 4 && words[i + 3] == 12: // OpVariable StorageBuffer
                    variables.Add((words[i + 2], words[i + 1]));
                    break;
            }
            i += count;
        }
        var result = new Dictionary<uint, uint>();
        foreach (var variable in variables)
            if (bindings.TryGetValue(variable.Id, out var binding) && pointers.TryGetValue(variable.Type, out var block) &&
                arraysByBlock.TryGetValue(block, out var array) && strides.TryGetValue(array, out var stride))
                result[binding] = stride;
        return result;
    }

    public static byte[] Prepare(ReadOnlySpan<byte> source, ISet<uint>? depthBindings = null, IReadOnlyDictionary<uint, PixelFormat>? storageFormats = null)
    {
        source = WebGpuShaderPackage.GetSpirV(source);
        if (source.Length < 20 || source.Length % 4 != 0 || BinaryPrimitives.ReadUInt32LittleEndian(source) != 0x07230203)
            throw new ArgumentException("Expected a little-endian SPIR-V module.", nameof(source));
        var words = MemoryMarshal.Cast<byte, uint>(source);
        var output = new List<uint>(words.Length);
        for (var i = 0; i < 5; i++) output.Add(words[i]);
        for (var i = 5; i < words.Length;)
        {
            var length = checked((int)(words[i] >> 16));
            var opcode = words[i] & 0xffff;
            if (length == 0 || length > words.Length - i) throw new ArgumentException("Invalid SPIR-V instruction length.", nameof(source));
            // SPV_GOOGLE_hlsl_functionality1 semantic/user-type string decorations are
            // reflection metadata. Numeric Location/Binding/Offset decorations remain.
            var decorationIndex = opcode == 5632 ? 2 : opcode == 5633 ? 3 : -1;
            // Stride also emits source-line markers among type declarations; Naga
            // rejects those placements. They have no executable meaning.
            var discard = opcode is 8 or 317 || (decorationIndex >= 0 && length > decorationIndex && words[i + decorationIndex] is 5635 or 5636);
            if (!discard) for (var j = 0; j < length; j++) output.Add(words[i + j]);
            i += length;
        }
        output = SpecializeStorageImages(output, storageFormats);
        output = Lower1DTextures(output);
        output = LowerReadOnlyValueParameters(output);
        var knownDepthBindings = ComparisonDepthBindings(output);
        if (depthBindings != null) knownDepthBindings.UnionWith(depthBindings);
        if (knownDepthBindings.Count > 0) output = SpecializeDepth(output, knownDepthBindings);
        return MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(output)).ToArray();
    }

    internal static string WgslVariantKey(byte[] source, ISet<uint>? depthBindings = null, IReadOnlyDictionary<uint, PixelFormat>? storageFormats = null)
    {
        var eligibleDepth = NullableFloat2DTextures(source);
        if (depthBindings is null) eligibleDepth.Clear();
        else eligibleDepth.IntersectWith(depthBindings);
        var resources = ResourceBindings(source);
        var formats = new List<KeyValuePair<uint, uint>>();
        if (storageFormats is not null)
            foreach (var format in storageFormats)
                if (resources.Contains(format.Key)) formats.Add(new(format.Key, (uint)format.Value));
        return WebGpuShaderPackage.VariantKey(eligibleDepth, formats);
    }

    private static HashSet<uint> ComparisonDepthBindings(List<uint> source)
    {
        // Declare comparison images as depth before Naga reads any fetch from
        // them; its late comparison-type inference would invalidate earlier loads.
        var bindings = new Dictionary<uint, uint>();
        var values = new Dictionary<uint, uint>();
        var sampledImages = new Dictionary<uint, uint>();
        var result = new HashSet<uint>();
        var words = CollectionsMarshal.AsSpan(source);
        for (var i = 5; i < words.Length; i += (int)(words[i] >> 16))
        {
            var op = words[i] & 0xffff;
            var count = (int)(words[i] >> 16);
            if (op == 71 && count == 4 && words[i + 2] == 33) bindings[words[i + 1]] = words[i + 3];
            if (op == 59) values[words[i + 2]] = words[i + 2];
            if (op is 61 or 83 && values.TryGetValue(words[i + 3], out var variable)) values[words[i + 2]] = variable;
            if (op == 86 && values.TryGetValue(words[i + 3], out variable)) sampledImages[words[i + 2]] = variable;
            if (op is 89 or 90 or 93 or 94 or 97 && sampledImages.TryGetValue(words[i + 3], out variable)
                && bindings.TryGetValue(variable, out var binding)) result.Add(binding);
        }
        return result;
    }

    private static List<uint> SpecializeDepth(List<uint> source, ISet<uint> depthBindings)
    {
        var words = CollectionsMarshal.AsSpan(source);
        var bindings = new Dictionary<uint, uint>();
        var types = new Dictionary<uint, uint[]>();
        for (var i = 5; i < words.Length; i += (int)(words[i] >> 16))
        {
            var count = (int)(words[i] >> 16);
            var opcode = words[i] & 0xffff;
            if (opcode == 71 && count == 4 && words[i + 2] == 33) bindings[words[i + 1]] = words[i + 3];
            if (opcode is 25 or 27 or 32) types[words[i + 1]] = words.Slice(i, count).ToArray();
        }
        var output = new List<uint>(source.Count);
        output.AddRange(source.GetRange(0, 5));
        var nextId = words[3];
        var typeClones = new Dictionary<uint, uint>();
        var values = new Dictionary<uint, uint>();
        var emittedTypes = new HashSet<uint>();
        uint CloneType(uint id)
        {
            if (typeClones.TryGetValue(id, out var cached)) return cached;
            var type = (uint[])types[id].Clone();
            switch (type[0] & 0xffff)
            {
                case 25: // OpTypeImage
                    if (type[7] != 1) throw new NotSupportedException("Only sampled images can bind depth textures.");
                    if (type[4] == 1) { typeClones[id] = id; return id; }
                    type[4] = 1;
                    break;
                case 27: type[2] = CloneType(type[2]); break; // OpTypeSampledImage
                case 32: type[3] = CloneType(type[3]); break; // OpTypePointer
                default: throw new NotSupportedException("This depth texture resource type is not supported.");
            }
            foreach (var existing in types.Values)
                if ((existing[0] & 0xffff) == (type[0] & 0xffff) && existing.AsSpan(2).SequenceEqual(type.AsSpan(2)))
                {
                    typeClones[id] = existing[1];
                    if (emittedTypes.Add(existing[1])) output.AddRange(existing);
                    return existing[1];
                }
            type[1] = nextId++;
            typeClones[id] = type[1];
            output.AddRange(type);
            emittedTypes.Add(type[1]);
            return type[1];
        }
        for (var i = 5; i < words.Length;)
        {
            var count = (int)(words[i] >> 16);
            var instruction = words.Slice(i, count).ToArray();
            if ((instruction[0] & 0xffff) is 25 or 27 or 32 && !emittedTypes.Add(instruction[1])) { i += count; continue; }
            switch (instruction[0] & 0xffff)
            {
                case 59 when count >= 4: // OpVariable
                    if (bindings.TryGetValue(instruction[2], out var binding) && depthBindings.Contains(binding))
                    {
                        var imageType = types[instruction[1]][3];
                        if (!types.TryGetValue(imageType, out var image) || (image[0] & 0xffff) != 25)
                            throw new NotSupportedException("Depth texture binding arrays are not supported.");
                        instruction[1] = CloneType(instruction[1]);
                        // Types must precede functions. Predeclare sampled-image
                        // and pointer forms before the specialized global.
                        foreach (var type in types.Values)
                            if (((type[0] & 0xffff) == 27 && type[2] == imageType) || ((type[0] & 0xffff) == 32 && type[3] == imageType)) CloneType(type[1]);
                        values[instruction[2]] = instruction[1];
                    }
                    break;
                case 61 when count >= 4: // OpLoad
                case 83 when count == 4: // OpCopyObject
                case 86 when count == 5: // OpSampledImage
                case 100 when count == 4: // OpImage
                    if (values.ContainsKey(instruction[3]))
                    {
                        instruction[1] = CloneType(instruction[1]);
                        values[instruction[2]] = instruction[1];
                    }
                    break;
                case 57: // OpFunctionCall: opaque resource arguments need inlining.
                    for (var argument = 4; argument < count; argument++)
                        if (values.ContainsKey(instruction[argument]))
                            throw new NotSupportedException("Depth texture function arguments must be inlined by the WebGPU shader compiler.");
                    break;
            }
            output.AddRange(instruction);
            i += count;
        }
        output[3] = nextId;
        return output;
    }
}
#endif
