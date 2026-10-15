using System.Diagnostics;

namespace Stride.Editor.ImGui;

internal readonly record struct ParityBenchmarkFrameToken(long Started, long Allocated, int Thread);
internal readonly record struct ParityBenchmarkFrameSample(double CpuMilliseconds, long AllocatedBytes);
internal sealed record ParityBenchmarkRecordedStage(string Name, string Boundary, double Milliseconds);
internal sealed record ParityBenchmarkRecordedFrames(string Boundary, string AllocationBoundary, int WarmupFrames,
    IReadOnlyList<ParityBenchmarkFrameSample> Samples, int[] CollectionsByGeneration);

/// <summary>Opt-in source hook for the exact same CPU boundary in either host; never measures GPU presentation.</summary>
internal sealed class ParityBenchmarkRecorder
{
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private readonly List<ParityBenchmarkFrameSample> samples = [];
    private readonly List<ParityBenchmarkRecordedStage> stages = [];
    private readonly int[] baselineCollections = new int[3];
    private string boundary = "";
    private int warmupFrames, warmupRemaining, maximumFrames;
    private ParityBenchmarkFrameToken? activeFrame;
    internal bool Recording { get; private set; }
    internal void Start(string cpuBoundary, int warmup, int maximum)
    {
        Check(); ArgumentException.ThrowIfNullOrWhiteSpace(cpuBoundary);
        if (warmup < 0 || maximum <= 0) throw new ArgumentOutOfRangeException(nameof(maximum));
        boundary = cpuBoundary; warmupFrames = warmupRemaining = warmup; maximumFrames = maximum;
        samples.Clear(); samples.EnsureCapacity(maximum); stages.Clear(); activeFrame = null; Recording = true;
        for (var generation = 0; generation < 3; generation++) baselineCollections[generation] = GC.CollectionCount(generation);
    }
    internal ParityBenchmarkFrameToken BeginFrame()
    {
        Check();
        if (!Recording) throw new InvalidOperationException("Explicit benchmark recording is required.");
        if (activeFrame != null) throw new InvalidOperationException("Benchmark CPU scopes cannot overlap.");
        var token = new ParityBenchmarkFrameToken(Stopwatch.GetTimestamp(), GC.GetAllocatedBytesForCurrentThread(), ownerThread);
        activeFrame = token;
        return token;
    }
    internal void EndFrame(ParityBenchmarkFrameToken token)
    {
        Check();
        if (!Recording || token.Thread != ownerThread || activeFrame != token) throw new InvalidOperationException("Benchmark frame ownership changed.");
        activeFrame = null;
        var elapsed = Stopwatch.GetElapsedTime(token.Started).TotalMilliseconds;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - token.Allocated;
        if (warmupRemaining > 0)
        {
            if (--warmupRemaining == 0)
                for (var generation = 0; generation < 3; generation++) baselineCollections[generation] = GC.CollectionCount(generation);
            return;
        }
        if (samples.Count >= maximumFrames) throw new InvalidOperationException("The bounded benchmark sample count is complete.");
        samples.Add(new(elapsed, allocated));
    }
    internal void RecordStage(string name, string cpuBoundary, long actualStartTimestamp)
    {
        Check(); ArgumentException.ThrowIfNullOrWhiteSpace(name); ArgumentException.ThrowIfNullOrWhiteSpace(cpuBoundary);
        if (!Recording) throw new InvalidOperationException("Explicit benchmark recording is required.");
        stages.Add(new(name, cpuBoundary, Stopwatch.GetElapsedTime(actualStartTimestamp).TotalMilliseconds));
    }
    internal object Finish()
    {
        Check();
        if (!Recording || activeFrame != null || samples.Count == 0) throw new InvalidOperationException("Actual post-warmup frame samples are required.");
        Recording = false;
        return new
        {
            frames = new ParityBenchmarkRecordedFrames(boundary,
                "current owning-thread managed allocation delta inside the declared CPU boundary; excludes other threads, native allocations and GPU resources",
                warmupFrames, samples.ToArray(), Enumerable.Range(0, 3).Select(generation => GC.CollectionCount(generation) - baselineCollections[generation]).ToArray()),
            stages = stages.ToArray(),
            limitation = "GC collection counts are process-wide; frame allocation is current-thread only. Stopwatch measures CPU submission, not GPU completion/present or native input-to-present latency.",
        };
    }
    private void Check()
    {
        if (Environment.CurrentManagedThreadId != ownerThread)
            throw new InvalidOperationException("Benchmark scopes must stay on their actual owning thread.");
    }
}
