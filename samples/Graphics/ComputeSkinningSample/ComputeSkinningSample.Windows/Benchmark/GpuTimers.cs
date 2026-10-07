using System.Diagnostics;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;

// D3D11 benchmark adapter: per-frame disjoint intervals work on either engine branch.
// Native device/context handles are borrowed from Stride; only queries are owned here.
sealed unsafe class GpuTimers : IDisposable, IGpuTimestampRecorder
{
    private const int QueryCapacity = 128;
    private const int RingCapacity = 12;
    private readonly ComPtr<ID3D11Device> device;
    private readonly ComPtr<ID3D11DeviceContext> context;
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

    public GpuTimers(GraphicsDevice graphics, string mode)
    {
        device = GraphicsMarshal.GetNativeDevice(graphics);
        context = GraphicsMarshal.GetNativeDeviceContext(graphics);
#if NALA
        expectedRegions = ["Scene", "Deformation"];
#else
        expectedRegions = ["Scene", "Deformation", mode == "dense" ? "DenseSkinning" : "SparseSkinning"];
#endif
        expectedRegions = [.. expectedRegions, "ControlUpload", "ShadowMaps"];
        try
        {
            for (int slot = 0; slot < ring.Length; slot++)
            {
                ring[slot] = new Slot();
                ring[slot].Disjoint = CreateQuery(Query.TimestampDisjoint);
                for (int query = 0; query < QueryCapacity; query++) ring[slot].Queries[query] = CreateQuery(Query.Timestamp);
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
            context.Begin(slot.Disjoint);
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
        context.End(current.Queries[index]);
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
        context.End(current.Disjoint);
        current.Pending = true;
        current = null;
    }

    public void Poll(bool drain = false)
    {
        foreach (var slot in ring)
        {
            if (slot == null || !slot.Pending || !drain && sequence - slot.Sequence < 3) continue;
            QueryDataTimestampDisjoint interval = default;
            HResult ready = context.GetData(slot.Disjoint, ref interval, (uint)sizeof(QueryDataTimestampDisjoint), (uint)AsyncGetdataFlag.Donotflush);
            if (ready.IsFailure) ready.Throw();
            if (ready != 0) continue;
            if ((bool)interval.Disjoint || interval.Frequency == 0 || interval.Frequency > long.MaxValue)
            {
                if (slot.Measured) Results(slot.CaseId).InvalidFrames++;
                slot.Pending = false;
                continue;
            }
            bool complete = true;
            for (int query = 0; query < slot.Used; query++)
            {
                HResult result = context.GetData(slot.Queries[query], ref slot.Values[query], sizeof(long), (uint)AsyncGetdataFlag.Donotflush);
                if (result.IsFailure) result.Throw();
                if (result != 0) { complete = false; break; }
            }
            if (!complete) continue;
            slot.Pending = false;
            if (!slot.Measured) continue;
            var totals = expectedRegions.ToDictionary(name => name, _ => 0.0);
            bool valid = true;
            foreach (var region in slot.Regions)
            {
                long start = slot.Values[region.Start], end = slot.Values[region.End];
                if (end < start) { valid = false; break; }
                totals[region.Name] += (end - start) * 1000.0 / interval.Frequency;
            }
            if (!valid) { Results(slot.CaseId).InvalidFrames++; continue; }
            var results = Results(slot.CaseId);
            results.Frequency = interval.Frequency;
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
            backend = "Direct3D11 timestamp queries", asynchronous = true, queryRingCapacity = RingCapacity,
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
        foreach (var slot in ring)
        {
            if (slot == null) continue;
            for (int index = 0; index < slot.Queries.Length; index++) slot.Queries[index].Dispose();
            slot.Disjoint.Dispose();
        }
    }

    private sealed class Slot
    {
        public ComPtr<ID3D11Query> Disjoint;
        public readonly ComPtr<ID3D11Query>[] Queries = new ComPtr<ID3D11Query>[QueryCapacity];
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

sealed class GpuTimingBoundaryProcessor : EntityProcessor<ModelComponent, ModelComponent>
{
    private readonly GpuTimers timer;
    private readonly bool begin;
    public GpuTimingBoundaryProcessor(GpuTimers timer, bool begin, int order) { this.timer = timer; this.begin = begin; Order = order; }
    protected override ModelComponent GenerateComponentData(Entity entity, ModelComponent component) => component;
    protected override bool IsAssociatedDataValid(Entity entity, ModelComponent component, ModelComponent data) => component == data;
    public override void Draw(RenderContext context) { if (begin) timer.BeginDeformation(); else timer.EndDeformation(); }
}
