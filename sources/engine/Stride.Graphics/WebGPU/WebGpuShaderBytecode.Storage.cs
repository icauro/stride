// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
#if STRIDE_GRAPHICS_API_WEBGPU
#nullable enable annotations
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Stride.Graphics;

internal static partial class WebGpuShaderBytecode
{
    // Stride's RWTexture declarations have ImageFormat Unknown. WebGPU requires
    // a concrete storage format. Clone image/pointer types per binding so two
    // resources sharing one SPIR-V type can still use different view formats.
    private static List<uint> SpecializeStorageImages(List<uint> source, IReadOnlyDictionary<uint, PixelFormat>? formats)
    {
        var words = CollectionsMarshal.AsSpan(source);
        var types = new Dictionary<uint, uint[]>();
        var bindings = new Dictionary<uint, uint>();
        for (var i = 5; i < words.Length; i += (int)(words[i] >> 16))
        {
            var count = (int)(words[i] >> 16);
            var op = words[i] & 0xffff;
            if (op is 21 or 22 or 25 or 32) types[words[i + 1]] = words.Slice(i, count).ToArray();
            if (op == 71 && count == 4 && words[i + 2] == 33) bindings[words[i + 1]] = words[i + 3];
        }
        var output = new List<uint>(source.Count);
        output.AddRange(source.GetRange(0, 5));
        var nextId = words[3];
        var values = new Dictionary<uint, uint>();
        var clones = new Dictionary<(uint Type, uint Format), uint>();
        uint Clone(uint id, uint format)
        {
            if (clones.TryGetValue((id, format), out var result)) return result;
            var type = (uint[])types[id].Clone();
            if ((type[0] & 0xffff) == 25) type[8] = format;
            else type[3] = Clone(type[3], format);
            type[1] = nextId++;
            output.AddRange(type);
            clones[(id, format)] = type[1];
            return type[1];
        }
        for (var i = 5; i < words.Length;)
        {
            var count = (int)(words[i] >> 16);
            var instruction = words.Slice(i, count).ToArray();
            var op = instruction[0] & 0xffff;
            // Every storage image below receives a format, so the unformatted
            // access capabilities are no longer required (and Naga rejects them).
            if (op == 17 && instruction[1] is 55 or 56) { i += count; continue; }
            if (op == 25 && instruction[7] == 2 && instruction[8] == 0)
            {
                var scalar = types[instruction[2]];
                instruction[8] = (scalar[0] & 0xffff) == 22 ? 1u : scalar[3] == 1 ? 21u : 30u;
            }
            if (op == 59 && bindings.TryGetValue(instruction[2], out var binding)
                && types.TryGetValue(instruction[1], out var pointer) && (pointer[0] & 0xffff) == 32
                && types.TryGetValue(pointer[3], out var image) && (image[0] & 0xffff) == 25 && image[7] == 2 && image[8] == 0)
            {
                if (formats != null && formats.TryGetValue(binding, out var format))
                {
                    var nativeFormat = StorageImageFormat(format);
                    instruction[1] = Clone(instruction[1], nativeFormat);
                    values[instruction[2]] = nativeFormat;
                }
            }
            if (op is 61 or 83 && count >= 4 && values.TryGetValue(instruction[3], out var valueFormat))
            {
                instruction[1] = Clone(instruction[1], valueFormat);
                values[instruction[2]] = valueFormat;
            }
            output.AddRange(instruction);
            i += count;
        }
        if (clones.Keys.Any(key => key.Format is 6 or 15 or 25 or 35) && !ContainsCapability(source, 49))
            output.InsertRange(5, new uint[] { (2u << 16) | 17u, 49 }); // StorageImageExtendedFormats
        output[3] = nextId;
        return output;
    }

    private static bool ContainsCapability(List<uint> words, uint capability)
    {
        for (var i = 5; i < words.Count; i += (int)(words[i] >> 16))
            if ((words[i] & 0xffff) == 17 && words[i + 1] == capability) return true;
        return false;
    }

    private static uint StorageImageFormat(PixelFormat format) => format switch
    {
        PixelFormat.R32G32B32A32_Float => 1, PixelFormat.R16G16B16A16_Float => 2,
        PixelFormat.R32_Float => 3, PixelFormat.R8G8B8A8_UNorm => 4, PixelFormat.R8G8B8A8_SNorm => 5,
        PixelFormat.R32G32_Float => 6, PixelFormat.R8_UNorm => 15,
        PixelFormat.R32G32B32A32_SInt => 21, PixelFormat.R16G16B16A16_SInt => 22,
        PixelFormat.R8G8B8A8_SInt => 23, PixelFormat.R32_SInt => 24, PixelFormat.R32G32_SInt => 25,
        PixelFormat.R32G32B32A32_UInt => 30, PixelFormat.R16G16B16A16_UInt => 31,
        PixelFormat.R8G8B8A8_UInt => 32, PixelFormat.R32_UInt => 33, PixelFormat.R32G32_UInt => 35,
        _ => throw new NotSupportedException($"WebGPU storage image format {format} is not implemented.")
    };
}
#endif
