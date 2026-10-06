// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;

namespace Stride.Rendering;

/// <summary>Optional GPU timestamp instrumentation supplied by a benchmark or profiling host.</summary>
public interface IGpuTimestampRecorder
{
    IDisposable BeginRegion(string name);
}
