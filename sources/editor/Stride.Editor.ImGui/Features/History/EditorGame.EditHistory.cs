using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool showEditHistory = true;
    private void DrawEditHistory()
    {
        if (!showEditHistory) return;
        Gui.SetNextWindowSize(new Vector2(UiScale(400), UiScale(320)), ImGuiCond.FirstUseEver);
        var document = CommandDocument as IEditorHistoryDocument;
        var scope = assetDeletionCommandContext ? HistoryTarget.Deletion : packageFolderCommandContext ? HistoryTarget.Folders : assetTagBatchCommandContext ? HistoryTarget.Tags
            : assetRelocationCommandContext ? HistoryTarget.Relocation : HistoryTarget.Document;
        var history = sessionHistory?.History ?? HistorySnapshot(scope, document);
        Guid? navigate = null;
        using (var window = ImGuiScopes.Window(EditorLocalization.Widget("Edit history", "View"), ref showEditHistory))
        {
            if (!window.DrawContents) return;
            if (Gui.IsWindowDocked()) dockedPanels++;
            if (history != null) navigate = DrawHistoryRows(history);
        }
        if (navigate is { } transaction)
            Execute(() => { if (sessionHistory != null) NavigateSessionHistory(transaction);
                else if (scope == HistoryTarget.Document && document != null) NavigateDocumentHistory(document, transaction);
                else NavigateGroupedHistory(scope, transaction); });
    }
    private Guid? DrawHistoryRows(EditorHistorySnapshot history)
    {
        using var child = ImGuiScopes.Child("History actions", new Vector2(0, 0), ImGuiChildFlags.None, ImGuiWindowFlags.None);
        if (!child.DrawContents) return null;
        Guid? navigate = null;
        var clipper = Gui.ImGuiListClipper();
        try
        {
            var iconSize = UiScale(16);
            var rowHeight = Math.Max(Gui.GetTextLineHeight(), iconSize);
            Gui.Begin(clipper, history.Entries.Count, rowHeight + Gui.GetStyle().ItemSpacing.Y);
            while (Gui.Step(clipper))
            for (var index = clipper.DisplayStart; index < clipper.DisplayEnd; index++)
            {
                var entry = history.Entries[index]; Gui.PushID(entry.Id.ToString());
                try
                {
                    var origin = Gui.GetCursorScreenPos();
                    var width = Math.Max(0, Gui.GetContentRegionAvail().X);
                    if (Gui.Selectable("##HistoryAction", history.Position == index + 1, ImGuiSelectableFlags.None, new Vector2(0, rowHeight))) navigate = entry.Id;
                    if (visualCapture != null) RegisterVisualCaptureItem("editor.history.transaction." + entry.Id.ToString("N"), "transaction", entry.Name);
                    var drawList = Gui.GetWindowDrawList();
                    var textOrigin = origin + new Vector2(0, (rowHeight - Gui.GetTextLineHeight()) * 0.5f);
                    var textWidth = Math.Max(0, width - iconSize - Gui.GetStyle().ItemInnerSpacing.X);
                    var color = Gui.GetColorU32(entry.IsDone ? ImGuiCol.Text : ImGuiCol.TextDisabled);
                    if (CanDrawNativeWidgetText)
                    {
                        var label = (nativeWidgetText ??= new()).Get(entry.Name, Gui.GetFontSize(), Gui.GetStyle().FontScaleDpi,
                            Math.Max(1, textWidth), false, NativeWidgetWeight, color);
                        PaintNativeWidget(label, textOrigin, origin, origin + new Vector2(textWidth, rowHeight));
                    }
                    else
                    {
                        drawList.PushClipRect(origin, origin + new Vector2(textWidth, rowHeight), true);
                        try { drawList.AddText(textOrigin, color, entry.Name); }
                        finally { drawList.PopClipRect(); }
                    }
                    if (entry.IsSavePoint && icons != null)
                    {
                        var iconOrigin = origin + new Vector2(Math.Max(0, width - iconSize), (rowHeight - iconSize) * 0.5f);
                        drawList.AddImage(icons.Get("save"), iconOrigin, iconOrigin + new Vector2(iconSize));
                    }
                }
                finally { Gui.PopID(); }
            }
        }
        finally { Gui.Destroy(clipper); }
        return navigate;
    }
    private void NavigateSessionHistory(Guid transaction)
    {
        if (IsPackageDependencyBusy || IsPackagePlatformUpdating) return;
        PrepareHistoryNavigation();
        for (var remaining = sessionHistory!.History.Entries.Count * 2 + 1; remaining > 0; remaining--)
        {
            var snapshot = sessionHistory.History;
            var target = -1;
            for (var index = 0; index < snapshot.Entries.Count; index++)
                if (snapshot.Entries[index].Id == transaction) { target = index + 1; break; }
            if (target < 0) throw new InvalidOperationException("This history state is no longer available.");
            if (snapshot.Position == target) return;
            if (snapshot.Position < target) RedoCurrentCommand(); else UndoCurrentCommand();
            if (sessionHistory.History.Position == snapshot.Position)
            { if (emptyWorkspaceReplay != null) emptyHistoryNavigationTarget = transaction; return; }
        }
        throw new InvalidOperationException("This history state could not be reached.");
    }

    private EditorHistorySnapshot? HistorySnapshot(HistoryTarget scope, IEditorHistoryDocument? document) => scope switch
    {
        HistoryTarget.Tags => assetTagBatchHistory.History, HistoryTarget.Relocation => assetRelocationHistory.History,
        HistoryTarget.Folders => packageFolderHistory.History, HistoryTarget.Deletion => assetDeletionHistory.History, _ => document?.History,
    };

    private void NavigateGroupedHistory(HistoryTarget scope, Guid transaction)
    {
        PrepareHistoryNavigation();
        var initial = HistorySnapshot(scope, null);
        if (initial == null) return;
        for (var remaining = initial.Entries.Count * 2 + 1; remaining > 0; remaining--)
        {
            var snapshot = HistorySnapshot(scope, null)!;
            var target = -1;
            for (var index = 0; index < snapshot.Entries.Count; index++)
                if (snapshot.Entries[index].Id == transaction) { target = index + 1; break; }
            if (target < 0) throw new InvalidOperationException("This history state is no longer available.");
            if (snapshot.Position == target) return;
            var redo = snapshot.Position < target;
            if (CurrentHistoryTarget(redo) != scope) throw new InvalidOperationException("This history operation cannot be applied.");
            if (redo) RedoCurrentCommand(); else UndoCurrentCommand();
            if (HistorySnapshot(scope, null)!.Position == snapshot.Position)
                throw new InvalidOperationException("This history operation cannot be applied.");
        }
        throw new InvalidOperationException("This history state could not be reached.");
    }

    private void NavigateDocumentHistory(IEditorHistoryDocument document, Guid transaction)
    {
        PrepareHistoryNavigation();
        if (!ReferenceEquals(CommandDocument, document)) return;
        for (var remaining = document.History.Entries.Count * 2 + 1; remaining > 0; remaining--)
        {
            var snapshot = document.History;
            var target = -1;
            for (var index = 0; index < snapshot.Entries.Count; index++)
                if (snapshot.Entries[index].Id == transaction) { target = index + 1; break; }
            if (target < 0) throw new InvalidOperationException("This history state is no longer available.");
            if (snapshot.Position == target) return;
            var redo = snapshot.Position < target;
            var ownership = CurrentHistoryTarget(redo);
            if (ownership is HistoryTarget.None or HistoryTarget.Blocked or HistoryTarget.Folders)
                throw new InvalidOperationException("This history operation cannot be applied.");
            if (ownership == HistoryTarget.Relocation && assetRelocationHistory.GetOwnership(document, redo) != GroupedHistoryOwnership.Ready
                || ownership == HistoryTarget.Tags && assetTagBatchHistory.GetOwnership(document, redo) != GroupedHistoryOwnership.Ready
                || ownership == HistoryTarget.Deletion && assetDeletionHistory.GetOwnership(document, redo) != GroupedHistoryOwnership.Ready)
                throw new InvalidOperationException("This history operation cannot be applied.");
            if (redo) RedoCurrentCommand(); else UndoCurrentCommand();
            if (!ReferenceEquals(CommandDocument, document) || document.History.Position == snapshot.Position)
                throw new InvalidOperationException("This history operation cannot be applied.");
        }
        throw new InvalidOperationException("This history state could not be reached.");
    }

    private void PrepareHistoryNavigation()
    {
        ValidatePendingPropertyStrings();
        FinishEntityRename(true);
        documents.CommitAll();
    }
}
