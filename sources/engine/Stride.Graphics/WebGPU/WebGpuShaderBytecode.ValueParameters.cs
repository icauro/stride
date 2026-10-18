// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
#if STRIDE_GRAPHICS_API_WEBGPU
using System.Collections.Generic;

namespace Stride.Graphics;

internal static partial class WebGpuShaderBytecode
{
    // Stride represents ordinary shader arguments as Function pointers. On the
    // pinned native Vulkan path a nested read-only scalar/vector argument can become
    // zero, while the same validated module works through native D3D12. Pass
    // values when every use is a plain load. Writable/reference, indexed and
    // other aggregate parameters keep their original calling convention.
    private static List<uint> LowerReadOnlyValueParameters(List<uint> source)
    {
        var valueTypes = new HashSet<uint>();
        var pointers = new Dictionary<uint, uint>();
        var signatures = new Dictionary<uint, uint[]>();
        var functionTypes = new Dictionary<uint, uint>();
        var parameters = new Dictionary<uint, (uint Function, int Index, uint Type)>();
        uint function = 0;
        var parameterIndex = 0;
        for (var i = 5; i < source.Count; i += (int)(source[i] >> 16))
        {
            var count = (int)(source[i] >> 16);
            switch (source[i] & 0xffff)
            {
                case 20: valueTypes.Add(source[i + 1]); break; // bool
                case 21 or 22 when source[i + 2] == 32: valueTypes.Add(source[i + 1]); break;
                case 23 when valueTypes.Contains(source[i + 2]): valueTypes.Add(source[i + 1]); break;
                case 32 when source[i + 2] == 7: pointers[source[i + 1]] = source[i + 3]; break;
                case 33: signatures[source[i + 1]] = source.GetRange(i, count).ToArray(); break;
                case 54:
                    function = source[i + 2]; parameterIndex = 0;
                    functionTypes[function] = source[i + 4];
                    break;
                case 55:
                    if (pointers.TryGetValue(source[i + 1], out var type) && valueTypes.Contains(type))
                        parameters[source[i + 2]] = (function, parameterIndex, type);
                    parameterIndex++;
                    break;
            }
        }
        if (parameters.Count == 0) return source;

        var eligible = new HashSet<uint>(parameters.Keys);
        for (var i = 5; i < source.Count; i += (int)(source[i] >> 16))
        {
            var count = (int)(source[i] >> 16);
            var opcode = source[i] & 0xffff;
            if (opcode is 2 or 3 or 4 or 5 or 6 or 7 or 55) continue; // source/debug names and declarations
            for (var operand = 1; operand < count; operand++)
            {
                var id = source[i + operand];
                if (eligible.Contains(id) && !(opcode == 61 && count == 4 && operand == 3))
                    eligible.Remove(id);
            }
        }
        if (eligible.Count == 0) return source;

        var lowered = new Dictionary<uint, Dictionary<int, uint>>();
        foreach (var id in eligible)
        {
            var parameter = parameters[id];
            if (!lowered.TryGetValue(parameter.Function, out var arguments))
                lowered[parameter.Function] = arguments = new Dictionary<int, uint>();
            arguments[parameter.Index] = parameter.Type;
        }
        var nextId = source[3];
        var declarations = new List<uint>();
        var newSignatures = new Dictionary<uint, uint>();
        foreach (var (functionId, arguments) in lowered)
        {
            var signature = (uint[])signatures[functionTypes[functionId]].Clone();
            signature[1] = nextId++;
            foreach (var (index, type) in arguments) signature[index + 3] = type;
            newSignatures[functionId] = signature[1];
            declarations.AddRange(signature);
        }

        var result = source.GetRange(0, 5);
        var declared = false;
        for (var i = 5; i < source.Count; i += (int)(source[i] >> 16))
        {
            var instruction = source.GetRange(i, (int)(source[i] >> 16)).ToArray();
            switch (instruction[0] & 0xffff)
            {
                case 54:
                    if (!declared) { result.AddRange(declarations); declared = true; }
                    if (newSignatures.TryGetValue(instruction[2], out var signature)) instruction[4] = signature;
                    break;
                case 55 when eligible.Contains(instruction[2]):
                    instruction[1] = parameters[instruction[2]].Type;
                    break;
                case 61 when instruction.Length == 4 && eligible.Contains(instruction[3]):
                    instruction[0] = (4u << 16) | 83; // load(pointer argument) -> copy(value argument)
                    break;
                case 57 when lowered.TryGetValue(instruction[3], out var arguments):
                    foreach (var (index, type) in arguments)
                    {
                        var value = nextId++;
                        result.AddRange(new[] { (4u << 16) | 61, type, value, instruction[index + 4] });
                        instruction[index + 4] = value;
                    }
                    break;
            }
            result.AddRange(instruction);
        }
        result[3] = nextId;
        return result;
    }
}
#endif
