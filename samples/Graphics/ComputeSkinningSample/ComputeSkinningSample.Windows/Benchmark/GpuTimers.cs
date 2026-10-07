using System.Diagnostics;
#if !VULKAN
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
#endif
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;

// Per-frame GPU timestamp intervals; the query backend depends on the graphics API.
sealed class GpuTimers : IDisposable, IGpuTimestampRecorder
{
    private const int QueryCapacity = 512;
    private const int RingCapacity = 12;
    private readonly ITimestampBackend backend;
    private readonly Slot[] ring = new Slot[RingCapacity];
    private readonly Dictionary<int, CaseResults> cases = new();
    private readonly string[] expectedRegions;
    private readonly Stack<(string Name, int Start)> scopes = new();
    private Slot current;
    private IDisposable deformation;
    private long deformationCpuStart;
    private long sequence;
    private int cursor;
    private bool disposed;

    public GpuTimers(GraphicsDevice graphics, GraphicsContext graphicsContext, string mode)
    {
        expectedRegions = ["Scene", "Deformation", mode == "dense" ? "DenseSkinning" : "SparseSkinning"];
        expectedRegions = [.. expectedRegions, "ControlUpload", "ShadowMaps"];
#if VULKAN
        backend = new StrideTimestampBackend(graphics, graphicsContext, RingCapacity, QueryCapacity);
#else
        backend = new D3D11TimestampBackend(graphics, RingCapacity, QueryCapacity);
#endif
        for (int slot = 0; slot < ring.Length; slot++) ring[slot] = new Slot { Index = slot };
    }

    public void BeginFrame(int caseId, int frame, bool measured)
    {
        if (current != null) throw new InvalidOperationException("GPU frame already open.");
        sequence++;
        Poll();
        var results = Results(caseId);
        if (measured) results.RequestedFrames++;
        for (int attempt = 0; attempt < ring.Length; attempt++)
        {
            var slot = ring[cursor]; cursor = (cursor + 1) % ring.Length;
            if (slot.Pending) continue;
            current = slot;
            slot.CaseId = caseId; slot.Frame = frame; slot.Measured = measured; slot.Sequence = sequence;
            slot.Used = 0; slot.Regions.Clear();
            backend.Begin(slot.Index);
            BeginRegion("Scene");
            return;
        }
        // Never block or grow indefinitely if GPU completion falls behind the CPU.
        if (measured) results.DroppedBusyFrames++;
    }

    public IDisposable BeginRegion(string name)
    {
        if (current == null) return EmptyScope.Instance;
        if (current.Used + scopes.Count + 2 > QueryCapacity) throw new InvalidOperationException("GPU timestamp capacity exceeded.");
        int start = WriteTimestamp();
        scopes.Push((name, start));
        return new RegionScope(this, current.Sequence, name, start);
    }

    private int WriteTimestamp()
    {
        int index = current.Used++;
        backend.Write(current.Index, index);
        return index;
    }

    private void EndRegion(long ownerSequence, string name, int start)
    {
        if (current == null || current.Sequence != ownerSequence) return;
        if (scopes.Count == 0 || scopes.Peek() != (name, start)) throw new InvalidOperationException("GPU timestamp scopes must close in order.");
        scopes.Pop();
        current.Regions.Add(new Region(name, start, WriteTimestamp()));
    }

    public void BeginDeformation() { deformationCpuStart = Stopwatch.GetTimestamp(); deformation = BeginRegion("Deformation"); }
    public void EndDeformation() { if (deformation != null && current?.Measured == true) Results(current.CaseId).CpuDeformation.Add(Stopwatch.GetElapsedTime(deformationCpuStart).TotalMilliseconds); deformation?.Dispose(); deformation = null; }

    public void EndFrame()
    {
        if (current == null) return;
        EndDeformation();
        while (scopes.Count != 0)
        {
            var scope = scopes.Peek();
            EndRegion(current.Sequence, scope.Name, scope.Start);
        }
        backend.End(current.Index, current.Used);
        current.Pending = true;
        current = null;
    }

    public void Poll(bool drain = false)
    {
        foreach (var slot in ring)
        {
            if (slot == null || !slot.Pending || !drain && sequence - slot.Sequence < 3) continue;
            if (!backend.TryRead(slot.Index, slot.Used, slot.Values, out ulong frequency)) continue;
            slot.Pending = false;
            if (frequency == 0 || frequency > long.MaxValue)
            {
                if (slot.Measured) Results(slot.CaseId).InvalidFrames++;
                continue;
            }
            if (!slot.Measured) continue;
            var totals = expectedRegions.ToDictionary(name => name, _ => 0.0);
            bool valid = true;
            foreach (var region in slot.Regions)
            {
                long start = slot.Values[region.Start], end = slot.Values[region.End];
                if (end < start) { valid = false; break; }
                totals[region.Name] += (end - start) * 1000.0 / frequency;
            }
            if (!valid) { Results(slot.CaseId).InvalidFrames++; continue; }
            var results = Results(slot.CaseId);
            results.Frequency = frequency;
            results.ValidFrames++;
            foreach (var pair in totals) results.Values[pair.Key].Add(pair.Value);
            results.FrameIds.Add(slot.Frame);
        }
    }

    public bool HasPending(int caseId) => ring.Any(slot => slot?.Pending == true && slot.CaseId == caseId);

    private CaseResults Results(int caseId)
    {
        if (!cases.TryGetValue(caseId, out var results)) cases[caseId] = results = new CaseResults(expectedRegions);
        return results;
    }

    public object Report(int caseId)
    {
        var results = Results(caseId);
        if (results.RequestedFrames != results.ValidFrames + results.InvalidFrames + results.DroppedBusyFrames || HasPending(caseId))
            throw new InvalidOperationException("GPU sample accounting incomplete.");
        return new {
            backend = backend.Name, asynchronous = true, queryRingCapacity = RingCapacity,
            requestedFrames = results.RequestedFrames, validFrames = results.ValidFrames,
            invalidFrames = results.InvalidFrames, droppedBusyFrames = results.DroppedBusyFrames,
            timestampFrequencyHz = results.Frequency,
            cpuDeformation = Stats(results.CpuDeformation),
            regions = results.Values.ToDictionary(pair => pair.Key, pair => Stats(pair.Value)),
            measuredFrameIds = results.FrameIds.Order().ToArray(),
        };
    }

    private static object Stats(List<double> values)
    {
        var sorted = values.Order().ToArray();
        return new { samples = sorted.Length, meanMs = sorted.Length == 0 ? (double?)null : sorted.Average(),
            medianMs = sorted.Length == 0 ? (double?)null : sorted[sorted.Length / 2],
            p95Ms = sorted.Length == 0 ? (double?)null : sorted[(int)((sorted.Length - 1) * 0.95)] };
    }

    public void ResetCase(int caseId)
    {
        if (HasPending(caseId)) throw new InvalidOperationException("Cannot reset pending GPU samples.");
        cases.Remove(caseId);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        backend.Dispose();
    }

    private sealed class Slot
    {
        public int Index;
        public readonly long[] Values = new long[QueryCapacity];
        public readonly List<Region> Regions = new(QueryCapacity / 2);
        public int CaseId, Frame, Used;
        public long Sequence;
        public bool Measured, Pending;
    }
    private sealed record Region(string Name, int Start, int End);
    private sealed class CaseResults(string[] labels)
    {
        public int RequestedFrames, ValidFrames, InvalidFrames, DroppedBusyFrames;
        public readonly List<double> CpuDeformation = [];
        public ulong Frequency;
        public readonly List<int> FrameIds = [];
        public readonly Dictionary<string, List<double>> Values = labels.ToDictionary(name => name, _ => new List<double>());
    }
    private sealed class RegionScope(GpuTimers owner, long sequence, string name, int start) : IDisposable
    {
        private bool disposed;
        public void Dispose() { if (disposed) return; disposed = true; owner.EndRegion(sequence, name, start); }
    }
    private sealed class EmptyScope : IDisposable
    {
        public static readonly EmptyScope Instance = new();
        public void Dispose() { }
    }
}

interface ITimestampBackend : IDisposable
{
    string Name { get; }
    void Begin(int slot);
    void Write(int slot, int index);
    void End(int slot, int used);
    // False until ready. A zero frequency marks an invalid (disjoint) interval.
    bool TryRead(int slot, int used, long[] values, out ulong frequency);
}

#if VULKAN
// Stride query pools. Vulkan reports a pool only once every query in it was written.
sealed class StrideTimestampBackend : ITimestampBackend
{
    private readonly GraphicsDevice device;
    private readonly GraphicsContext context;
    private readonly QueryPool[] pools;
    private readonly int capacity;

    public StrideTimestampBackend(GraphicsDevice device, GraphicsContext context, int slots, int capacity)
    {
        this.device = device; this.context = context; this.capacity = capacity;
        pools = new QueryPool[slots];
        for (int slot = 0; slot < slots; slot++) pools[slot] = QueryPool.New(device, QueryType.Timestamp, capacity);
    }

    public string Name => "Vulkan timestamp queries";
    public void Begin(int slot) => context.CommandList.ResetQueryPool(pools[slot]);
    public void Write(int slot, int index) => context.CommandList.WriteTimestamp(pools[slot], index);
    public void End(int slot, int used) { for (int index = used; index < capacity; index++) Write(slot, index); }

    public bool TryRead(int slot, int used, long[] values, out ulong frequency)
    {
        frequency = 0;
        if (!pools[slot].TryGetData(values)) return false;
        frequency = (ulong)device.TimestampFrequency;
        return true;
    }

    public void Dispose() { foreach (var pool in pools) pool?.Dispose(); }
}
#else
// Native device/context handles are borrowed from Stride; only queries are owned here.
sealed unsafe class D3D11TimestampBackend : ITimestampBackend
{
    private readonly ComPtr<ID3D11Device> device;
    private readonly ComPtr<ID3D11DeviceContext> context;
    private readonly ComPtr<ID3D11Query>[] disjoint;
    private readonly ComPtr<ID3D11Query>[][] queries;

    public D3D11TimestampBackend(GraphicsDevice graphics, int slots, int capacity)
    {
        device = GraphicsMarshal.GetNativeDevice(graphics);
        context = GraphicsMarshal.GetNativeDeviceContext(graphics);
        disjoint = new ComPtr<ID3D11Query>[slots];
        queries = new ComPtr<ID3D11Query>[slots][];
        try
        {
            for (int slot = 0; slot < slots; slot++)
            {
                disjoint[slot] = CreateQuery(Query.TimestampDisjoint);
                queries[slot] = new ComPtr<ID3D11Query>[capacity];
                for (int query = 0; query < capacity; query++) queries[slot][query] = CreateQuery(Query.Timestamp);
            }
        }
        catch { Dispose(); throw; }
    }

    private ComPtr<ID3D11Query> CreateQuery(Query kind)
    {
        var description = new QueryDesc { Query = kind };
        ComPtr<ID3D11Query> query = default;
        HResult result = device.CreateQuery(in description, ref query);
        if (result.IsFailure) result.Throw();
        return query;
    }

    public string Name => "Direct3D11 timestamp queries";
    public void Begin(int slot) => context.Begin(disjoint[slot]);
    public void Write(int slot, int index) => context.End(queries[slot][index]);
    public void End(int slot, int used) => context.End(disjoint[slot]);

    public bool TryRead(int slot, int used, long[] values, out ulong frequency)
    {
        frequency = 0;
        QueryDataTimestampDisjoint interval = default;
        HResult ready = context.GetData(disjoint[slot], ref interval, (uint)sizeof(QueryDataTimestampDisjoint), (uint)AsyncGetdataFlag.Donotflush);
        if (ready.IsFailure) ready.Throw();
        if (ready != 0) return false;
        for (int query = 0; query < used; query++)
        {
            HResult result = context.GetData(queries[slot][query], ref values[query], sizeof(long), (uint)AsyncGetdataFlag.Donotflush);
            if (result.IsFailure) result.Throw();
            if (result != 0) return false;
        }
        if (!(bool)interval.Disjoint) frequency = interval.Frequency;
        return true;
    }

    public void Dispose()
    {
        foreach (var slot in queries) if (slot != null) foreach (var query in slot) query.Dispose();
        if (disjoint != null) foreach (var query in disjoint) query.Dispose();
    }
}
#endif

sealed class GpuTimingBoundaryProcessor : EntityProcessor<ModelComponent, ModelComponent>
{
    private readonly GpuTimers timer;
    private readonly bool begin;
    public GpuTimingBoundaryProcessor(GpuTimers timer, bool begin, int order) { this.timer = timer; this.begin = begin; Order = order; }
    protected override ModelComponent GenerateComponentData(Entity entity, ModelComponent component) => component;
    protected override bool IsAssociatedDataValid(Entity entity, ModelComponent component, ModelComponent data) => component == data;
    public override void Draw(RenderContext context) { if (begin) timer.BeginDeformation(); else timer.EndDeformation(); }
}
