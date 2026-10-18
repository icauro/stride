// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

#if STRIDE_GRAPHICS_API_WEBGPU

using System;

namespace Stride.Graphics
{
    public partial class GraphicsOutput
    {
        public DisplayMode FindClosestMatchingDisplayMode(ReadOnlySpan<GraphicsProfile> targetProfiles, DisplayMode mode)
        {
            WebGpuUnsupported.Throw();
            return default;
        }
        private void InitializeSupportedDisplayModes()
        {
            WebGpuUnsupported.Throw();
        }
        private void InitializeCurrentDisplayMode()
        {
            WebGpuUnsupported.Throw();
        }
    }
}

#endif
