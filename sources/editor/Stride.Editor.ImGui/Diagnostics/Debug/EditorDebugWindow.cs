using System.Diagnostics;
using Hexa.NET.ImGui;
using Stride.Core.Assets.Quantum;
using Stride.Core.Quantum;
using Stride.Core.Transactions;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed record EditorDebugRoot(string Name, AssetPropertyGraph Graph, IDisposable? Lease = null) : IDisposable
{ public void Dispose() => Lease?.Dispose(); }
internal interface IEditorDebugHost
{
    EditorOutputLog Log { get; }
    EditorSessionHistory? History { get; }
    IReadOnlyList<EditorDebugRoot> Graphs();
    void DrainFailedGraphAcquisitions();
    float Scale(float dips);
    void DrawSelectableText(string id, string value);
}

/// <summary>Reference debug pages over the actual logger, transaction stack and canonical Quantum graph.</summary>
internal sealed class EditorDebugWindow(IEditorDebugHost host) : IDisposable
{
    public bool Visible;
    private EditorSessionHistory? observedHistory;
    private long logVersion = -1;
    private EditorOutputEntry[] log = [];
    private string[] formattedLog = [];
    private IReadOnlyList<EditorDebugRoot> roots = [];
    private readonly List<EditorDebugRoot> retiredRoots = [];
    private readonly List<(Guid Id, string Type, DiscardReason Reason)> discarded = [];
    public void Draw()
    {
        host.DrainFailedGraphAcquisitions();
        BindHistory();
        DrainRetiredRoots();
        if (!Visible) return;
        Gui.SetNextWindowSize(new NVector2(host.Scale(1024), host.Scale(768)), ImGuiCond.FirstUseEver);
        using var window = ImGuiScopes.Window("DebugWindow", ref Visible);
        if (!window.DrawContents) return;
        if (!Gui.BeginTabBar("Debug pages")) return;
        try
        {
            DrawTab("Log", DrawLog);
            DrawTab("Undo/Redo", DrawHistory);
            DrawTab("Asset Nodes", DrawGraphs);
        }
        finally { Gui.EndTabBar(); }
    }
    private static void DrawTab(string name, Action draw)
    {
        if (!Gui.BeginTabItem(name)) return;
        try { draw(); } finally { Gui.EndTabItem(); }
    }
    private void BindHistory()
    {
        if (ReferenceEquals(observedHistory, host.History)) return;
        if (observedHistory != null) observedHistory.Stack.TransactionDiscarded -= OnDiscarded;
        observedHistory = host.History; discarded.Clear();
        RetireRoots([]);
        if (observedHistory != null) observedHistory.Stack.TransactionDiscarded += OnDiscarded;
    }
    private void OnDiscarded(object? sender, TransactionsDiscardedEventArgs args)
    {
        foreach (var transaction in args.Transactions) discarded.Add((transaction.Id, transaction.GetType().Name, args.Reason));
        if (discarded.Count > 10000) discarded.RemoveRange(0, discarded.Count - 10000);
    }
    private void DrawLog()
    {
        if (host.Log.Read(ref logVersion, out var current))
        {
            var formatted = new string[current.Length];
            var unchanged = 0;
            while (unchanged < current.Length && unchanged < log.Length && ReferenceEquals(current[unchanged], log[unchanged])) unchanged++;
            Array.Copy(formattedLog, formatted, unchanged);
            for (var index = unchanged; index < current.Length; index++) formatted[index] = current[index].DisplayText(showStacktrace: true);
            log = current;
            formattedLog = formatted;
        }
        using var logBody = ImGuiScopes.Child("DebugLog", NVector2.Zero, ImGuiChildFlags.None, ImGuiWindowFlags.None);
        if (!logBody.DrawContents) return;
        for (var index = 0; index < log.Length; index++)
        {
            Gui.PushID(index);
            try { host.DrawSelectableText("##Log", formattedLog[index]); }
            finally { Gui.PopID(); }
        }
    }
    private void DrawHistory()
    {
        if (Gui.Button("Clear discarded items")) discarded.Clear();
        if (!Gui.BeginTable("DebugTransactions", 2, ImGuiTableFlags.BordersInnerV)) return;
        try
        {
            Gui.TableSetupColumn("Current action items", ImGuiTableColumnFlags.WidthStretch, 1f, 0u);
            Gui.TableSetupColumn("Discarded action items", ImGuiTableColumnFlags.WidthStretch, 1f, 0u);
            Gui.TableHeadersRow(); Gui.TableNextRow(); Gui.TableSetColumnIndex(0);
            var names = observedHistory?.History.Entries.ToDictionary(entry => entry.Id, entry => entry.Name) ?? [];
            foreach (var transaction in observedHistory?.Stack.RetrieveAllTransactions() ?? [])
            {
                Gui.PushID(transaction.Id.ToString());
                try
                {
                    if (Gui.TreeNodeEx(names.GetValueOrDefault(transaction.Id) ?? transaction.GetType().Name, ImGuiTreeNodeFlags.DefaultOpen))
                    {
                        try { foreach (var operation in transaction.Operations) DrawOperation(operation); }
                        finally { Gui.TreePop(); }
                    }
                }
                finally { Gui.PopID(); }
            }
            Gui.TableSetColumnIndex(1);
            foreach (var entry in discarded) host.DrawSelectableText(entry.Id.ToString(), entry.Type + " (" + entry.Reason + ") " + entry.Id);
        }
        finally { Gui.EndTable(); }
    }
    private void DrawOperation(Operation operation)
    {
        Gui.PushID(operation.Id.ToString());
        try
        {
            if (Gui.TreeNodeEx(operation.GetType().Name, ImGuiTreeNodeFlags.None))
            {
                try
                {
                    host.DrawSelectableText("##Id", operation.Id.ToString());
                    if (operation is IReadOnlyTransaction transaction) foreach (var child in transaction.Operations) DrawOperation(child);
                }
                finally { Gui.TreePop(); }
            }
        }
        finally { Gui.PopID(); }
    }
    private void DrawGraphs()
    {
        if (Gui.Button("Refresh") || roots.Count == 0)
        { var next = host.Graphs(); RetireRoots(next); DrainRetiredRoots(); }
        foreach (var root in roots)
        {
            Gui.PushID(root.Graph.AuthoredItem.Asset.Id.ToString());
            try
            {
                if (Gui.TreeNodeEx("[" + root.Graph.AuthoredItem.Asset.GetType().Name + "] " + root.Name, ImGuiTreeNodeFlags.None))
                {
                    try { DrawNode(root.Graph.RootNode, new HashSet<IGraphNode>(ReferenceEqualityComparer.Instance)); }
                    finally { Gui.TreePop(); }
                }
            }
            finally { Gui.PopID(); }
        }
    }
    private void DrawNode(IGraphNode node, HashSet<IGraphNode> path)
    {
        if (!path.Add(node)) return;
        Gui.PushID(node.Guid.ToString());
        try
        {
            var name = node is IMemberNode member ? member.Name : node.Type.Name;
            var open = Gui.TreeNodeEx(name + " (" + node.Type.Name + ")", ImGuiTreeNodeFlags.None);
            if (open)
            {
                try
                {
                    host.DrawSelectableText("##Value", node.Retrieve()?.ToString() ?? "null");
                    using (ImGuiScopes.Disabled(!System.Diagnostics.Debugger.IsAttached)) if (Gui.Button("Break here...")) System.Diagnostics.Debugger.Break();
                    if (node is IAssetNode { BaseNode: { } source }) host.DrawSelectableText("##Base", "Base: " + (source.Retrieve()?.ToString() ?? "null"));
                    if (node is IMemberNode { Target: { } target }) DrawNode(target, path);
                    if (node is IObjectNode owner)
                    {
                        foreach (var child in owner.Members) DrawNode(child, path);
                        foreach (var index in owner.Indices ?? [])
                        {
                            Gui.PushID(index.ToString());
                            try
                            {
                                if (owner.IndexedTarget(index) is { } child) DrawNode(child, path);
                                else host.DrawSelectableText("##Item", "[" + index + "] " + (owner.Retrieve(index)?.ToString() ?? "null"));
                            }
                            finally { Gui.PopID(); }
                        }
                    }
                }
                finally { Gui.TreePop(); }
            }
        }
        finally { Gui.PopID(); path.Remove(node); }
    }
    public void Dispose()
    {
        if (observedHistory != null) observedHistory.Stack.TransactionDiscarded -= OnDiscarded;
        observedHistory = null; discarded.Clear(); RetireRoots([]); Visible = false;
        log = []; formattedLog = []; logVersion = -1;
        List<Exception>? errors = null;
        try { DrainRetiredRoots(); }
        catch (Exception error) { (errors ??= []).Add(error); }
        try { host.DrainFailedGraphAcquisitions(); }
        catch (Exception error) { (errors ??= []).Add(error); }
        if (errors != null) throw new AggregateException(errors);
    }

    private void RetireRoots(IReadOnlyList<EditorDebugRoot> next)
    {
        retiredRoots.AddRange(roots);
        roots = next;
    }

    private void DrainRetiredRoots()
    {
        List<Exception>? errors = null;
        for (var index = retiredRoots.Count - 1; index >= 0; index--)
        {
            try { retiredRoots[index].Dispose(); retiredRoots.RemoveAt(index); }
            catch (Exception error) { (errors ??= []).Add(error); }
        }
        if (errors != null) throw new AggregateException(errors);
    }
}
