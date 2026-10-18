// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
#if STRIDE_GRAPHICS_API_WEBGPU
using System;
using System.Runtime.CompilerServices;
namespace Stride.Graphics;
internal static class WebGpuUnsupported
{
    public static void Throw([CallerMemberName] string operation = "") =>
        throw new NotSupportedException($"WebGPU backend operation is not implemented yet: {operation}.");
}
#endif
