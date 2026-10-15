using System.Numerics;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed unsafe partial class ScriptTextEditor
{
    private ScriptCodeActionItem[] bulbActions = [];
    private long discoveryAfter, discoveryVersion = -1;
    private int discoveryCaret = -1;
    private Microsoft.CodeAnalysis.Text.TextSpan discoverySelection;
    private bool discoveryRequested, bulbVisible;
    private long discoveryEpoch = -1;
    private ScriptSemanticWorkspace? discoveryWorkspace;
    private Guid? discoveryUndo, discoveryRedo;
    private Vector2 bulbPosition, sourceOrigin;
    public void RetryCodeActionDiscovery()
    {
        discoveryRequested = false; bulbActions = []; discoveryAfter = Environment.TickCount64 + 400;
    }
    public void SuppressCodeActionDiscovery() { discoveryRequested = true; bulbActions = []; }
    public void ObserveCodeActionContext(ScriptSemanticWorkspace? workspace, long epoch, Guid? undo, Guid? redo)
    {
        var contextChanged = !ReferenceEquals(workspace, discoveryWorkspace) || epoch != discoveryEpoch;
        if (!contextChanged && undo == discoveryUndo && redo == discoveryRedo) return;
        discoveryWorkspace = workspace;
        discoveryEpoch = epoch; discoveryUndo = undo; discoveryRedo = redo;
        if (contextChanged)
        {
            DismissAssistance(); quickInfoText = ""; quickInfoVersion = -1; quickInfoRequestedPosition = -1;
            bracePairs = []; braceVersion = -1; braceRequestedVersion = -1; braceRequestedPosition = -1;
            completionDescription = ""; describedCompletion = -1;
        }
        RetryCodeActionDiscovery();
    }
    private void UpdateCodeActionDiscovery(ScriptSourceDocument document)
    {
        if (discoveryVersion != document.Version || discoveryCaret != caretPosition || discoverySelection != selection)
        {
            discoveryVersion = document.Version; discoveryCaret = caretPosition; discoverySelection = selection;
            RetryCodeActionDiscovery();
        }
        if (!csharp || !MayEditSource(document) || !InputActive || discoveryRequested || assistRequest != null
            || completion != null || completionPending || signatures.Length > 0 || definitions.Length > 0 || codeActions.Length > 0
            || codeActionPreview != null || Environment.TickCount64 < discoveryAfter) return;
        discoveryRequested = true;
        assistRequest = new(ScriptAssistKind.DiscoverCodeActions, caretPosition, document.Version, Selection: selection);
    }
    private void DrawCodeActionBulb(ScriptSourceDocument document)
    {
        if (!MayEditSource(document) || bulbActions.Length == 0 || !bulbVisible || codeActionPreview != null) return;
        var cursor = Gui.GetCursorScreenPos();
        var size = Math.Min(18 * Gui.GetStyle().FontScaleDpi, Gui.GetTextLineHeight());
        Gui.SetCursorScreenPos(bulbPosition);
        if (Gui.InvisibleButton("##Code action bulb" + label, new Vector2(size)))
        {
            completion = null; signatures = []; definitions = []; completionPending = false;
            assistRequest = null; codeActions = bulbActions; AssistanceGeneration++;
        }
        if (Gui.IsItemHovered()) Gui.SetTooltip("Quick Actions");
        var draw = Gui.GetWindowDrawList();
        var scale = size / 24;
        Vector2 Point(float x, float y) => bulbPosition + new Vector2(x, y) * scale;
        var color = Gui.GetColorU32(new Vector4(1, 0.82f, 0.3f, 1));
        draw.AddCircle(Point(12, 9), 5 * scale, color, 16, Math.Max(1, 1.7f * scale));
        draw.AddLine(Point(8, 13), Point(9, 17), color, Math.Max(1, 1.7f * scale));
        draw.AddLine(Point(16, 13), Point(15, 17), color, Math.Max(1, 1.7f * scale));
        draw.AddLine(Point(9, 17), Point(15, 17), color, Math.Max(1, 1.7f * scale));
        draw.AddLine(Point(9, 20), Point(15, 20), color, Math.Max(1, 1.7f * scale));
        draw.AddLine(Point(10, 22), Point(14, 22), color, Math.Max(1, 1.7f * scale));
        Gui.SetCursorScreenPos(cursor);
    }
}
