// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
#if STRIDE_GRAPHICS_API_WEBGPU
using System;
namespace Stride.Graphics;
public partial class GraphicsResourceBase
{
    private partial void Initialize() { }
    protected internal virtual partial void OnDestroyed(bool immediately = false) => Destroyed?.Invoke(this, EventArgs.Empty);
    internal virtual void SwapInternal(GraphicsResourceBase other) { }
}
#endif
