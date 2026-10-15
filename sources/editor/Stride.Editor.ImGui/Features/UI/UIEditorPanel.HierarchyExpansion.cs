using Hexa.NET.ImGui;
using Stride.UI;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class UIEditorPanel
{
    private sealed class UIHierarchyExpansion(UIDocument source, UIHierarchyDragOwner owner, bool expanded) : IDisposable
    {
        internal readonly UIDocument Source = source;
        internal readonly UIHierarchyDragOwner Owner = owner;
        internal readonly long Revision = source.EditRevision, Layout = source.LayoutVersion;
        internal readonly bool Expanded = expanded;
        internal readonly Queue<Guid[]> Paths = new(source.Roots.Select(root => new[] { root.Id }));
        internal bool ResetScroll = !expanded;
        public void Dispose() => Owner.Dispose();
    }
    private UIHierarchyExpansion? hierarchyExpansion;
    private static string UIElementTreeLabel(UIElement element)
        => (string.IsNullOrEmpty(element.Name) ? element.GetType().Name : element.Name) + "###UIElementRow";
    private void CancelHierarchyExpansion()
    {
        var expansion = hierarchyExpansion; hierarchyExpansion = null; expansion?.Dispose();
    }
    private void DrawHierarchyExpansionCommands(UIDocument source)
    {
        host.DrawViewLabel("Visual Tree", true);
        if (host.DrawHierarchyExpansionButton("hierarchy-expand", "Expand all elements")) QueueExpansion(true);
        host.RecordDiagnosticControl("ui/expand");
        host.RegisterCaptureItem("ui.expand-all", "control", "Expand all elements");
        Gui.SameLine();
        if (host.DrawHierarchyExpansionButton("hierarchy-collapse", "Collapse all elements")) QueueExpansion(false);
        host.RegisterCaptureItem("ui.collapse-all", "control", "Collapse all elements");
        void QueueExpansion(bool expanded)
        {
            QueueUICommand(source, [], false, (asset, _) =>
            {
                var owner = host.AcquireHierarchyCommandOwner(asset, false);
                if (owner == null) return;
                try
                {
                    var prepared = new UIHierarchyExpansion(asset, owner, expanded);
                    CancelHierarchyExpansion(); hierarchyExpansion = prepared; owner = null;
                }
                finally { owner?.Dispose(); }
            });
        }
    }
    private void PumpHierarchyExpansion(UIDocument source)
    {
        if (hierarchyExpansion is not { } work) return;
        if (!ReferenceEquals(work.Source, source) || !work.Owner.IsCurrent
            || source.EditRevision != work.Revision || source.LayoutVersion != work.Layout)
        { CancelHierarchyExpansion(); return; }
        var storage = Gui.GetStateStorage();
        if (storage.IsNull) return;
        // Like the reference's idle-dispatched expansion, bound work per frame.
        for (var count = 0; count < 128 && work.Paths.TryDequeue(out var path); count++)
        {
            var pushes = 0;
            UIElement? element = null;
            try
            {
                for (var index = 0; index < path.Length; index++)
                {
                    element = source.Find(path[index]);
                    if (element == null) break;
                    Gui.PushID(element.Id.ToString()); pushes++;
                    var id = Gui.GetID(UIElementTreeLabel(element));
                    if (index == path.Length - 1) storage.SetInt(id, work.Expanded ? 1 : 0);
                    else { ImGuiP.PushOverrideID(id); pushes++; }
                }
            }
            finally { while (pushes-- > 0) Gui.PopID(); }
            if (element != null)
                foreach (var child in element.VisualChildren)
                    work.Paths.Enqueue([.. path, child.Id]);
        }
        if (work.ResetScroll) { Gui.SetScrollY(0); work.ResetScroll = false; }
        if (work.Paths.Count == 0) CancelHierarchyExpansion();
    }
}
