// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
#if STRIDE_GRAPHICS_API_WEBGPU
using System;
using System.Collections.Generic;

namespace Stride.Graphics;

internal static partial class WebGpuShaderBytecode
{
    // WebGPU native 1D storage has only one mip and cannot be rendered into.
    // Back all Stride 1D textures with height-one 2D images and preserve logical
    // coordinates and width queries here, without changing other shader backends.
    private static List<uint> Lower1DTextures(List<uint> source)
    {
        var images = new HashSet<uint>();
        var types = new HashSet<uint>();
        var scalarTypes = new HashSet<uint>();
        var floatTypes = new HashSet<uint>();
        var wrappers = new Dictionary<uint, uint>();
        var vectors = new Dictionary<(uint Scalar, uint Count), uint>();
        for (var i = 5; i < source.Count; i += (int)(source[i] >> 16))
        {
            var opcode = source[i] & 0xffff;
            if (opcode is >= 19 and <= 39) types.Add(source[i + 1]);
            if (opcode is 21 or 22) scalarTypes.Add(source[i + 1]);
            if (opcode == 22) floatTypes.Add(source[i + 1]);
            if (opcode == 23) vectors[(source[i + 2], source[i + 3])] = source[i + 1];
            if (opcode == 27) wrappers[source[i + 1]] = source[i + 2];
            if (opcode == 32) wrappers[source[i + 1]] = source[i + 3];
            if (opcode == 25 && source[i + 3] == 0)
            {
                if (source[i + 5] != 0 || source[i + 6] != 0)
                    throw new NotSupportedException("WebGPU 1D texture arrays and multisampling are not supported.");
                images.Add(source[i + 1]);
            }
        }
        if (images.Count == 0) return source;
        var values = new Dictionary<uint, uint>();
        for (var i = 5; i < source.Count; i += (int)(source[i] >> 16))
            if ((source[i] >> 16) >= 3 && types.Contains(source[i + 1])) values[source[i + 2]] = source[i + 1];
        bool Is1D(uint value)
        {
            if (!values.TryGetValue(value, out var type)) return false;
            while (wrappers.TryGetValue(type, out var wrapped)) type = wrapped;
            return images.Contains(type);
        }
        var nextId = source[3];
        var declarations = new List<uint>();
        uint VectorType(uint scalar)
        {
            if (!vectors.TryGetValue((scalar, 2), out var type))
            {
                type = nextId++;
                declarations.AddRange(new[] { (4u << 16) | 23, type, scalar, 2u });
                vectors[(scalar, 2)] = type;
            }
            return type;
        }
        var constants = new Dictionary<(uint Type, uint Bits), uint>();
        uint Constant(uint type, uint bits)
        {
            if (!constants.TryGetValue((type, bits), out var id))
            {
                id = nextId++;
                declarations.AddRange(new[] { (4u << 16) | 43, type, id, bits });
                constants[(type, bits)] = id;
            }
            return id;
        }
        uint Expand(uint value, bool normalized, bool constant, List<uint> before)
        {
            if (!values.TryGetValue(value, out var scalar) || !scalarTypes.Contains(scalar))
                throw new NotSupportedException("Expected a scalar WebGPU 1D texture coordinate or offset.");
            var type = VectorType(scalar);
            var second = Constant(scalar, normalized && floatTypes.Contains(scalar) ? 0x3f000000u : 0u);
            var id = nextId++;
            var instruction = new[] { (5u << 16) | (constant ? 44u : 80u), type, id, value, second };
            if (constant) declarations.AddRange(instruction); else before.AddRange(instruction);
            return id;
        }
        var body = new List<uint>();
        var declarationOffset = -1;
        for (var i = 5; i < source.Count; i += (int)(source[i] >> 16))
        {
            var instruction = source.GetRange(i, (int)(source[i] >> 16)).ToArray();
            var opcode = instruction[0] & 0xffff;
            if (opcode == 54 && declarationOffset < 0) declarationOffset = body.Count;
            if (opcode == 25 && images.Contains(instruction[1])) instruction[3] = 1; // Dim2D
            if (opcode == 17 && instruction[1] is 43 or 44) continue; // Sampled1D / Image1D capabilities
            var imageIndex = opcode == 99 ? 1 : 3;
            if (opcode is >= 87 and <= 107 && instruction.Length > imageIndex && Is1D(instruction[imageIndex]))
            {
                if (opcode is 103 or 104) // logical width = physical size.x
                {
                    var scalar = instruction[1]; var id = instruction[2]; var size = nextId++;
                    instruction[1] = VectorType(scalar); instruction[2] = size;
                    body.AddRange(instruction);
                    body.AddRange(new[] { (5u << 16) | 81, scalar, id, size, 0u });
                    continue;
                }
                if (opcode is >= 91 and <= 94 or 96 or 97)
                    throw new NotSupportedException("Projected sampling and gather on WebGPU 1D textures are not implemented.");
                if (opcode is 87 or 88 or 89 or 90 or 95 or 98 or 99 or 105)
                {
                    var coordinateIndex = opcode == 99 ? 2 : 4;
                    instruction[coordinateIndex] = Expand(instruction[coordinateIndex], opcode is 87 or 88 or 89 or 90 or 105, false, body);
                    var maskIndex = opcode is 89 or 90 or 99 ? 6 : 5;
                    if (opcode == 99) maskIndex = 4;
                    if (opcode != 105 && instruction.Length > maskIndex)
                    {
                        var mask = instruction[maskIndex];
                        if ((mask & ~0xdfu) != 0) throw new NotSupportedException("This WebGPU 1D image operand is not implemented.");
                        var operand = maskIndex + 1;
                        foreach (var bit in new[] { 1u, 2u, 4u, 8u, 16u, 64u, 128u })
                        {
                            if ((mask & bit) == 0) continue;
                            if (bit == 4) // gradients expand with a zero y derivative
                            {
                                instruction[operand] = Expand(instruction[operand], false, false, body); operand++;
                                instruction[operand] = Expand(instruction[operand], false, false, body); operand++;
                            }
                            else
                            {
                                if (bit is 8 or 16) instruction[operand] = Expand(instruction[operand], false, bit == 8, body);
                                operand++;
                            }
                        }
                    }
                }
            }
            body.AddRange(instruction);
        }
        if (declarationOffset < 0) declarationOffset = body.Count;
        body.InsertRange(declarationOffset, declarations);
        var result = source.GetRange(0, 5); result[3] = nextId; result.AddRange(body);
        return result;
    }
}
#endif
