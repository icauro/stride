using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed record BenchmarkNode(System.Numerics.Vector2 Size, System.Numerics.Vector2 SizeRef,
        System.Numerics.Vector2 Pos, bool Locked, int Authority, uint Parent, uint Child0, uint Child1);
    private sealed class BenchmarkGeometry(string window, EngineViewport viewport, int width, int height)
    {
        internal readonly string Window = window;
        internal readonly EngineViewport Viewport = viewport;
        internal readonly int Width = width, Height = height;
        internal readonly TaskCompletionSource<object> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Pass, Stable, LastFrame = -1;
        internal readonly Dictionary<uint, BenchmarkNode> Original = [];
        internal readonly List<object> Evidence = [];
        internal int OriginalWidth, OriginalHeight, RollbackFrame;
    }
    private BenchmarkGeometry? benchmarkGeometry;
    private BenchmarkGeometry? benchmarkRollback;
    private object? benchmarkGeometryEvidence;
    private object? InspectMatchedBenchmarkGeometry() => benchmarkGeometryEvidence;
    internal Task<object> BeginMatchedBenchmarkViewport(string nativeDocumentWindow, EngineViewport viewport, int width, int height)
    {
        if (!args.Contains("--matched-benchmark") || visualCapture == null)
            throw new InvalidOperationException("Geometry measurement requires explicit benchmark capture mode.");
        if (benchmarkGeometry != null || width is < 64 or > 4096 || height is < 64 or > 4096)
            throw new InvalidOperationException("A bounded benchmark geometry request must run alone.");
        if (!viewport.Visible || viewport.PresentedDocument == null) throw new InvalidOperationException("An actual visible scene owner is required.");
        benchmarkGeometry = new(nativeDocumentWindow, viewport, width, height);
        visualCapture.CancelPendingAction = CancelMatchedBenchmarkViewport;
        return benchmarkGeometry.Completion.Task;
    }
    // Parent pumps after the actual scene panel is laid out, once per native frame.
    internal unsafe void PumpMatchedBenchmarkViewport()
    {
        if (benchmarkRollback is { } rollback && Gui.GetFrameCount() >= rollback.RollbackFrame + 2)
        {
            var target = rollback.Viewport.Target;
            var nodes = rollback.Original.Select(pair =>
            {
                var node = ImGuiP.DockBuilderGetNode(pair.Key).Handle;
                var same = node != null
                    && (node->ParentNode == null ? 0 : node->ParentNode->ID) == pair.Value.Parent
                    && ((&node->ChildNodes_0)[0] == null ? 0 : (&node->ChildNodes_0)[0]->ID) == pair.Value.Child0
                    && ((&node->ChildNodes_0)[1] == null ? 0 : (&node->ChildNodes_0)[1]->ID) == pair.Value.Child1;
                return new { id = pair.Key, topologyPreserved = same, original = Describe(pair.Value), current = node == null ? null : Describe(Read(node)) };
            }).ToArray();
            benchmarkGeometryEvidence = new { outcome = "rollback-observed", passes = rollback.Evidence,
                restoredViewport = target != null && Math.Abs(target.Width - rollback.OriginalWidth) <= 1 && Math.Abs(target.Height - rollback.OriginalHeight) <= 1,
                originalWidth = rollback.OriginalWidth, originalHeight = rollback.OriginalHeight,
                actualWidth = target?.Width, actualHeight = target?.Height, observedFrame = Gui.GetFrameCount(), nodes };
            benchmarkRollback = null;
        }
        var request = benchmarkGeometry;
        if (request == null || request.LastFrame == Gui.GetFrameCount()) return;
        request.LastFrame = Gui.GetFrameCount();
        try
        {
            var window = ImGuiP.FindWindowByName(request.Window);
            if (window.IsNull || !window.WasActive || window.Hidden || window.DockId == 0
                || window.ViewportId != Gui.GetMainViewport().ID || !request.Viewport.Visible)
                throw new InvalidOperationException("Benchmark viewport must remain visible in its original main-window dock.");
            var target = request.Viewport.Target ?? throw new InvalidOperationException("The actual scene target is unavailable.");
            var leaf = ImGuiP.DockBuilderGetNode(window.DockId).Handle;
            if (request.Original.Count == 0)
            {
                var root = leaf;
                for (var depth = 0; root != null && root->ParentNode != null; depth++)
                { if (depth >= 64) throw new InvalidOperationException("Invalid native dock ancestry."); root = root->ParentNode; }
                Capture(root);
                request.OriginalWidth = target.Width; request.OriginalHeight = target.Height;
                void Capture(ImGuiDockNode* node)
                {
                    if (node == null) return;
                    if (request.Original.Count >= 128) throw new InvalidOperationException("Benchmark dock tree exceeds its bounded scope.");
                    request.Original.Add(node->ID, Read(node));
                    Capture((&node->ChildNodes_0)[0]); Capture((&node->ChildNodes_0)[1]);
                }
            }
            request.Evidence.Add(new { stage = "observed", frame = Gui.GetFrameCount(), pass = request.Pass,
                targetWidth = target.Width, targetHeight = target.Height, windowSize = Vector(window.Size),
                physicalViewport = visualCapture!.InspectMatchedBenchmarkViewport(request.Viewport.PresentedDocument!.Asset.Id.ToString()),
                nodes = request.Original.Keys.Select(id => { var node = ImGuiP.DockBuilderGetNode(id).Handle; return new { id, state = node == null ? null : Describe(Read(node)) }; }).ToArray() });
            benchmarkGeometryEvidence = new { outcome = "adjusting", passes = request.Evidence };
            if (Math.Abs(target.Width - request.Width) <= 1 && Math.Abs(target.Height - request.Height) <= 1)
            {
                if (++request.Stable < 2) return;
                var state = request.Viewport.CaptureMatchedBenchmarkState();
                var rectangle = visualCapture!.InspectMatchedBenchmarkViewport(request.Viewport.PresentedDocument!.Asset.Id.ToString());
                request.Completion.TrySetResult(new { requestedWidth = request.Width, requestedHeight = request.Height,
                    actualWidth = target.Width, actualHeight = target.Height, passes = request.Pass,
                    nativeWindow = request.Window, dockId = window.DockId, actualAuthority = state, actualPhysicalViewport = rectangle,
                    control = "native existing splitter sizes; actual scene target, no clamp or stretch", evidence = request.Evidence });
                benchmarkGeometryEvidence = new { outcome = "matched", passes = request.Evidence };
                benchmarkGeometry = null; return;
            }
            request.Stable = 0;
            if (++request.Pass > 12) throw new InvalidOperationException("Actual dock constraints prevented matching the requested viewport within 12 frames.");
            Adjust(0, request.Width - target.Width); Adjust(1, request.Height - target.Height);
            void Adjust(int axis, int deficit)
            {
                if (Math.Abs(deficit) <= 1) return;
                var branch = leaf;
                for (var depth = 0; branch != null && branch->ParentNode != null && depth < 64; depth++)
                {
                    var parent = branch->ParentNode;
                    if ((int)parent->SplitAxis == axis)
                    {
                        var a = (&parent->ChildNodes_0)[0]; var b = (&parent->ChildNodes_0)[1];
                        var other = a == branch ? b : a;
                        if (other == null) throw new InvalidOperationException("Native dock splitter has no sibling.");
                        var current = axis == 0 ? branch->Size.X : branch->Size.Y;
                        var sibling = axis == 0 ? other->Size.X : other->Size.Y;
                        var delta = deficit * .75f;
                        var minimum = axis == 0 ? Gui.GetStyle().WindowMinSize.X : Gui.GetStyle().WindowMinSize.Y;
                        var noResize = (ImGuiDockNodeFlags)(axis == 0 ? (int)ImGuiDockNodeFlagsPrivate.NoResizeX : (int)ImGuiDockNodeFlagsPrivate.NoResizeY);
                        if (((branch->MergedFlags | other->MergedFlags) & (ImGuiDockNodeFlags.NoResize | noResize)) != 0
                            || current + delta < minimum || sibling - delta < minimum)
                            throw new InvalidOperationException("Requested viewport violates existing visible pane constraints.");
                        var beforeBranch = Read(branch); var beforeOther = Read(other);
                        var nextBranch = branch->Size; var nextOther = other->Size;
                        if (axis == 0) { nextBranch.X = current + delta; nextOther.X = sibling - delta; }
                        else { nextBranch.Y = current + delta; nextOther.Y = sibling - delta; }
                        // Native 1.92.2b splitter writes both Size and SizeRef. Locking
                        // BOTH siblings discards edited refs using the old actual Size.
                        ImGuiP.DockBuilderSetNodeSize(branch->ID, nextBranch);
                        ImGuiP.DockBuilderSetNodeSize(other->ID, nextOther);
                        branch->WantLockSizeOnce = false; other->WantLockSizeOnce = false;
                        var child1 = (&parent->ChildNodes_0)[1];
                        if (axis == 0) child1->Pos.X = parent->Pos.X + a->Size.X + Gui.GetStyle().DockingSeparatorSize;
                        else child1->Pos.Y = parent->Pos.Y + a->Size.Y + Gui.GetStyle().DockingSeparatorSize;
                        request.Evidence.Add(new { stage = "splitter-write", frame = Gui.GetFrameCount(), axis, deficit, delta, minimum,
                            parent = parent->ID, parentState = Describe(Read(parent)), branch = branch->ID, sibling = other->ID,
                            beforeBranch = Describe(beforeBranch), beforeSibling = Describe(beforeOther),
                            afterBranch = Describe(Read(branch)), afterSibling = Describe(Read(other)) });
                        return;
                    }
                    branch = parent;
                }
                throw new InvalidOperationException("The actual scene layout has no adjustable splitter on the required axis.");
            }
        }
        catch (Exception error) { request.Completion.TrySetException(error); Restore(); benchmarkGeometry = null; }
        void Restore()
        {
            foreach (var pair in request.Original)
            {
                var node = ImGuiP.DockBuilderGetNode(pair.Key).Handle;
                if (node == null) continue;
                Write(node, pair.Value);
            }
            request.RollbackFrame = Gui.GetFrameCount(); benchmarkRollback = request;
            benchmarkGeometryEvidence = new { outcome = "rollback-requested", passes = request.Evidence, restoredStateObserved = false };
        }
    }
    internal unsafe void CancelMatchedBenchmarkViewport()
    {
        var request = benchmarkGeometry; if (request == null) return;
        foreach (var pair in request.Original)
        {
            var node = ImGuiP.DockBuilderGetNode(pair.Key).Handle;
            if (node == null) continue;
            Write(node, pair.Value);
        }
        request.RollbackFrame = Gui.GetFrameCount(); benchmarkRollback = request;
        benchmarkGeometryEvidence = new { outcome = "rollback-requested", passes = request.Evidence, restoredStateObserved = false };
        request.Completion.TrySetCanceled(); benchmarkGeometry = null;
    }
    private static unsafe BenchmarkNode Read(ImGuiDockNode* node) => new(node->Size, node->SizeRef, node->Pos,
        node->WantLockSizeOnce, (int)node->AuthorityForSize, node->ParentNode == null ? 0 : node->ParentNode->ID,
        (&node->ChildNodes_0)[0] == null ? 0 : (&node->ChildNodes_0)[0]->ID,
        (&node->ChildNodes_0)[1] == null ? 0 : (&node->ChildNodes_0)[1]->ID);
    private static unsafe void Write(ImGuiDockNode* node, BenchmarkNode value)
    { node->Size = value.Size; node->SizeRef = value.SizeRef; node->Pos = value.Pos; node->WantLockSizeOnce = value.Locked; node->AuthorityForSize = (ImGuiDataAuthority)value.Authority; }
    private static object Vector(System.Numerics.Vector2 value) => new { x = value.X, y = value.Y };
    private static object Describe(BenchmarkNode value) => new { size = Vector(value.Size), sizeRef = Vector(value.SizeRef),
        position = Vector(value.Pos), locked = value.Locked, authority = value.Authority, parent = value.Parent, child0 = value.Child0, child1 = value.Child1 };
}
