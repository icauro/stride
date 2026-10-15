using System.Diagnostics;

namespace Stride.Editor.ImGui;

internal enum EditorFrameStage { Update, Draw, Panels, NativeSceneUpdate, NativeSceneDraw,
    AuthoringMaintenance, PreviewMaintenance, SceneSynchronization, Hierarchy, Inspector, AssetBrowser,
    ImportPreviewPolling, CompositorPreviewPolling, TexturePreviewPolling, MaterialPreviewPolling,
    SkyboxPreviewPolling, FontPreviewPolling, AnimatedPreviewPolling, CatalogPreviewPolling,
    AssetImportPolling, ScriptSemanticPolling, VisualScriptPolling, ThumbnailRetirement, Count }

internal sealed partial class EditorGame
{
    private bool frameStageMeasurementsConfigured;
    private EditorFrameStageMeasurements? frameStageMeasurements;
    private EditorFrameStageScope BeginFrameStageUpdate()
    {
        if (!frameStageMeasurementsConfigured)
        {
            frameStageMeasurementsConfigured = true;
            if (args.Contains("--measure-frame-stages")) frameStageMeasurements = new();
        }
        if (frameStageMeasurements == null) return default;
        frameStageMeasurements.BeginFrame(projectLoading == null && projectContent != null
            && engineViewport?.RuntimeScene != null && engineViewport.Target != null
            && !IsEditorGpuRetirementPending && !IsEditorGraphicsRecovering);
        return MeasureEditorFrameStage(EditorFrameStage.Update);
    }
    private EditorFrameStageScope MeasureEditorFrameStage(EditorFrameStage stage)
        => frameStageMeasurements?.Recording == true ? new(frameStageMeasurements, stage) : default;
    private object? InspectFrameStageMeasurements() => frameStageMeasurements?.Snapshot();
    private object RestartFrameStageMeasurements()
    {
        if (!args.Contains("--measure-frame-stages")) throw new InvalidOperationException("Explicit --measure-frame-stages is required.");
        frameStageMeasurementsConfigured = true;
        frameStageMeasurements = new();
        return new { restarted = true, warmupUpdates = 60, maximumSamplesPerStage = 180 };
    }
}

// Aggregate storage is fixed: no per-frame samples, objects or strings are retained.
internal sealed class EditorFrameStageMeasurements
{
    private struct Aggregate { internal int Count; internal long Total, Minimum, Maximum, AllocatedTotal, AllocatedMinimum, AllocatedMaximum; }
    private readonly Aggregate[] stages = new Aggregate[(int)EditorFrameStage.Count];
    private int warmupRemaining = 60;
    private long started;
    private DateTimeOffset? startedUtc;
    private bool complete;
    private string? completionReason;
    private sealed record MemorySnapshot(long AllocatedBytes, long HeapSizeBytes, long FragmentedBytes, int Gen0, int Gen1, int Gen2);
    private MemorySnapshot? memoryStart, memoryEnd;
    private static MemorySnapshot ReadMemory()
    {
        var info = GC.GetGCMemoryInfo();
        return new(GC.GetTotalAllocatedBytes(false), info.HeapSizeBytes, info.FragmentedBytes,
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
    }
    internal bool Recording { get; private set; }
    internal void BeginFrame(bool ready)
    {
        Recording = false;
        if (complete || !ready) return;
        if (warmupRemaining > 0) { warmupRemaining--; return; }
        var now = Stopwatch.GetTimestamp();
        if (started == 0) { started = now; startedUtc = DateTimeOffset.UtcNow; memoryStart = ReadMemory(); }
        if (stages[(int)EditorFrameStage.Update].Count >= 180 || Stopwatch.GetElapsedTime(started, now).TotalSeconds >= 30)
        {
            complete = true;
            completionReason = stages[(int)EditorFrameStage.Update].Count >= 180 ? "sample-limit" : "time-limit";
            memoryEnd = ReadMemory();
            return;
        }
        Recording = true;
    }
    internal void Record(EditorFrameStage stage, long start, long allocatedStart)
    {
        ref var aggregate = ref stages[(int)stage];
        if (aggregate.Count >= 180) return;
        var elapsed = Stopwatch.GetTimestamp() - start;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedStart;
        aggregate.Total += elapsed;
        aggregate.Minimum = aggregate.Count == 0 ? elapsed : Math.Min(aggregate.Minimum, elapsed);
        aggregate.Maximum = Math.Max(aggregate.Maximum, elapsed);
        aggregate.AllocatedTotal += allocated;
        aggregate.AllocatedMinimum = aggregate.Count == 0 ? allocated : Math.Min(aggregate.AllocatedMinimum, allocated);
        aggregate.AllocatedMaximum = Math.Max(aggregate.AllocatedMaximum, allocated);
        aggregate.Count++;
    }
    internal object Snapshot() => new
    {
        enabled = true, complete, completionReason, warmupRemaining, warmupUpdates = 60,
        maximumSamplesPerStage = 180, maximumWindowSeconds = 30, startedUtc,
        memory = InspectMemory(),
        boundary = "Owner-thread Stopwatch elapsed and GC.GetAllocatedBytesForCurrentThread deltas inside each declared Update/Draw call or nested stage; includes blocking waits, excludes outer BeginDraw/EndDraw/present/throttle, other-thread CPU/allocation and GPU completion. Nested stages overlap outer scopes and must not be summed together. AssetBrowser covers the whole DrawWorkspacePanel boundary.",
        stages = Enumerable.Range(0, stages.Length).Select(index => new
        {
            name = ((EditorFrameStage)index).ToString(), samples = stages[index].Count,
            meanMilliseconds = stages[index].Count == 0 ? (double?)null : stages[index].Total * 1000d / Stopwatch.Frequency / stages[index].Count,
            minimumMilliseconds = stages[index].Count == 0 ? (double?)null : stages[index].Minimum * 1000d / Stopwatch.Frequency,
            maximumMilliseconds = stages[index].Count == 0 ? (double?)null : stages[index].Maximum * 1000d / Stopwatch.Frequency,
            totalCurrentThreadAllocatedBytes = stages[index].AllocatedTotal,
            meanCurrentThreadAllocatedBytes = stages[index].Count == 0 ? (double?)null : (double)stages[index].AllocatedTotal / stages[index].Count,
            minimumCurrentThreadAllocatedBytes = stages[index].Count == 0 ? (long?)null : stages[index].AllocatedMinimum,
            maximumCurrentThreadAllocatedBytes = stages[index].Count == 0 ? (long?)null : stages[index].AllocatedMaximum
        }).ToArray()
    };
    private object InspectMemory()
    {
        var end = memoryEnd ?? ReadMemory();
        return new
        {
            start = memoryStart, end, endIsFinal = memoryEnd != null,
            allocatedDeltaBytes = memoryStart == null ? (long?)null : end.AllocatedBytes - memoryStart.AllocatedBytes,
            generation0Collections = memoryStart == null ? (int?)null : end.Gen0 - memoryStart.Gen0,
            generation1Collections = memoryStart == null ? (int?)null : end.Gen1 - memoryStart.Gen1,
            generation2Collections = memoryStart == null ? (int?)null : end.Gen2 - memoryStart.Gen2,
            boundary = "Process-wide managed allocation counter and collection counts, including other threads and capture metadata. HeapSize/FragmentedBytes describe the last completed GC; neither is a live native/GPU allocation measurement. No GC is forced. Memory queries occur only at measurement start/end and requested metadata snapshots."
        };
    }
}

internal readonly ref struct EditorFrameStageScope
{
    private readonly EditorFrameStageMeasurements? owner;
    private readonly EditorFrameStage stage;
    private readonly long started;
    private readonly long allocatedStart;
    internal EditorFrameStageScope(EditorFrameStageMeasurements owner, EditorFrameStage stage)
    { this.owner = owner; this.stage = stage; allocatedStart = GC.GetAllocatedBytesForCurrentThread(); started = Stopwatch.GetTimestamp(); }
    public void Dispose() { owner?.Record(stage, started, allocatedStart); }
}
