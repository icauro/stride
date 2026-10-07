using System;

namespace Stride.Rendering;

/// <summary>Optional GPU timestamp instrumentation supplied by a benchmark or profiling host.</summary>
public interface IGpuTimestampRecorder
{
    IDisposable BeginRegion(string name);
}
