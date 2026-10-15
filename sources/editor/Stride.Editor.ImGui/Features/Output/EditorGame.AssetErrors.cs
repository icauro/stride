using Hexa.NET.ImGui;
using Stride.Core.Assets;
using Stride.Core.Assets.Analysis;
using Stride.Core.Diagnostics;
using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool showAssetErrors = true;
    private bool assetErrorWarnings = true, assetErrorErrors = true, assetErrorFatal = true, assetErrorStackTrace;
    private bool assetErrorDebug, assetErrorVerbose, assetErrorInfo;
    private long assetErrorLogVersion = -1, assetErrorSelectionVersion = -1;
    private ProjectWorkspace? assetErrorWorkspace;
    private EditorOutputEntry[] assetErrorEntries = [];
    private readonly List<AssetErrorRow> assetErrorRows = [];
    private readonly List<AssetErrorRow> visibleAssetErrorRows = [];
    private bool assetErrorFiltersDirty = true;
    private bool assetErrorLayoutDirty = true;
    private int[] assetErrorRowOffsets = [0];
    private float assetErrorLayoutWidth = -1, assetErrorLayoutFont = -1, assetErrorLayoutPadding = -1;
    private float assetErrorLayoutDpi = -1;
    private bool assetErrorLayoutNative;
    private readonly Dictionary<IAssetTagDocument, long> assetErrorDocumentRevisions = [];
    private AssetErrorRow? selectedAssetError;
    private sealed record AssetErrorRow(EditorOutputEntry Entry, AssetEntry Asset)
    {
        public float MessageHeight;
    }

    private void DrawAssetErrors()
    {
        if (!showAssetErrors) return;
        ObserveAssetErrorSources();
        MigrateLiveWindowTitle("Asset errors", "###Asset errors");
        Gui.SetNextWindowSize(new Vector2(UiScale(760), UiScale(200)), ImGuiCond.FirstUseEver);
        var errorCount = assetErrorRows.Count(row => row.Entry.Level >= LogMessageType.Error);
        var title = string.Format(System.Globalization.CultureInfo.CurrentUICulture,
            EditorLocalization.Plural("Asset error ({0})", "Asset errors ({0})", errorCount, "View"), errorCount);
        AssetEntry? navigate = null;
        using (var window = ImGuiScopes.Window(title + "###Asset errors", ref showAssetErrors))
        {
            if (!window.DrawContents) return;
            if (Gui.IsWindowDocked()) dockedPanels++;
            navigate = DrawAssetErrorContents();
        }
        if (navigate is { } target) Execute(() => NavigateAssetError(target));
    }

    private void NavigateAssetError(AssetEntry target)
    {
        var expected = workspace;
        if (expected == null) return;
        ValidatePendingPropertyStrings();
        FinishEntityRename(true);
        if (!ReferenceEquals(workspace, expected))
            throw new InvalidOperationException("The project changed while finishing pending edits.");
        if (assetBrowser.RevealAsset(expected, target.Id) is { } selectable)
        {
            selectedAssetPath = selectable.Path;
            ActivateAssetPropertyContext();
            showAssets = true; focusAssetsPanel = true;
        }
    }

    private void ObserveAssetErrorSources()
    {
        var changed = outputLog.ReadCurrentAssetDiagnostics(ref assetErrorLogVersion, out var entries);
        if (changed) assetErrorEntries = entries;
        changed |= !ReferenceEquals(assetErrorWorkspace, workspace) || assetErrorSelectionVersion != assetBrowser.SelectionVersion;
        var retainedCount = 0;
        foreach (var authoredDocument in documents.AuthoredDocuments)
            if (authoredDocument is IAssetTagDocument document)
            {
                retainedCount++;
                if (!assetErrorDocumentRevisions.TryGetValue(document, out var revision) || revision != document.EditRevision) changed = true;
            }
        changed |= retainedCount != assetErrorDocumentRevisions.Count;
        if (changed) Execute(() => RefreshAssetErrorRows(documents.AuthoredDocuments.OfType<IAssetTagDocument>().ToArray()));
    }

    private AssetEntry? DrawAssetErrorContents()
    {
        AssetEntry? navigate = null;
        DrawAssetErrorToolbar();
        if (assetErrorFiltersDirty)
        {
            visibleAssetErrorRows.Clear();
            foreach (var row in assetErrorRows)
                if (row.Entry.Level switch
                {
                    LogMessageType.Debug => assetErrorDebug, LogMessageType.Verbose => assetErrorVerbose,
                    LogMessageType.Info => assetErrorInfo,
                    LogMessageType.Warning => assetErrorWarnings, LogMessageType.Error => assetErrorErrors,
                    LogMessageType.Fatal => assetErrorFatal, _ => false,
                }) visibleAssetErrorRows.Add(row);
            assetErrorFiltersDirty = false;
            assetErrorLayoutDirty = true;
            if (selectedAssetError != null && !visibleAssetErrorRows.Contains(selectedAssetError)) selectedAssetError = null;
        }
        var visible = visibleAssetErrorRows;
        var available = Gui.GetContentRegionAvail();
        var detailsHeight = selectedAssetError == null ? 0 : Math.Min(available.Y * .4f, UiScale(180));
        if (Gui.BeginTable("Asset error messages", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.ScrollX | ImGuiTableFlags.Resizable,
            new Vector2(available.X, Math.Max(1, available.Y - detailsHeight - (detailsHeight > 0 ? Gui.GetStyle().ItemSpacing.Y : 0))),
            Math.Max(available.X, UiScale(360))))
        {
            try
            {
                Gui.TableSetupColumn("##Severity", ImGuiTableColumnFlags.WidthFixed, UiScale(16));
                Gui.TableSetupColumn("##Exception", ImGuiTableColumnFlags.WidthFixed, UiScale(25));
                Gui.TableSetupColumn("Message", ImGuiTableColumnFlags.WidthStretch);
                Gui.TableSetupColumn("Asset", ImGuiTableColumnFlags.WidthFixed,
                    Math.Min(UiScale(200), Math.Max(UiScale(80), available.X * .3f))); Gui.TableHeadersRow();
                Gui.TableSetColumnIndex(2);
                var messageWidth = Math.Max(1, Gui.GetContentRegionAvail().X);
                UpdateAssetErrorLayout(messageWidth);
                // Use the native clipper's pixel cursor seek to preserve the table's
                // full scroll extent while mapping its ranges through variable row heights.
                ImGuiP.TableEndRow(ImGuiP.GetCurrentTable());
                var clipper = Gui.ImGuiListClipper();
                try
                {
                    Gui.Begin(clipper, assetErrorRowOffsets[^1], 1);
                    var drawnThrough = 0;
                    while (Gui.Step(clipper))
                    {
                        var first = Math.Max(drawnThrough, FindAssetErrorRow(clipper.DisplayStart));
                        var end = Math.Min(visible.Count, FindAssetErrorRow(Math.Max(0, clipper.DisplayEnd - 1)) + 1);
                        if (first >= end) continue;
                        Gui.SeekCursorForItem(clipper, assetErrorRowOffsets[first]);
                        var table = ImGuiP.GetCurrentTable();
                        table.RowBgColorCounter = first;
                        for (var index = first; index < end; index++)
                        {
                            var row = visible[index]; Gui.PushID(index);
                            try
                            {
                                Gui.TableNextRow(ImGuiTableRowFlags.None, assetErrorRowOffsets[index + 1] - assetErrorRowOffsets[index]);
                                Gui.TableSetColumnIndex(0);
                                var origin = Gui.GetCursorScreenPos();
                                if (Gui.Selectable("##AssetError", ReferenceEquals(row, selectedAssetError), ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick,
                                    new Vector2(0, row.MessageHeight)))
                                {
                                    selectedAssetError = row;
                                    if (Gui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) navigate = row.Asset;
                                }
                                DrawAssetErrorSeverity(origin, row.Entry.Level);
                                Gui.TableNextColumn(); Gui.TextUnformatted(string.IsNullOrEmpty(row.Entry.Exception) ? "" : "(...)");
                                if (!string.IsNullOrEmpty(row.Entry.Exception) && row.Entry.ExceptionType is { Length: > 0 } exceptionType && Gui.IsItemHovered())
                                    Gui.SetTooltip(exceptionType);
                                if (Gui.TableNextColumn())
                                {
                                    if (CanDrawNativeWidgetText)
                                    {
                                        var textOrigin = Gui.GetCursorScreenPos();
                                        var layout = (nativeWidgetText ??= new()).Get(row.Entry.MessageText ?? row.Entry.Text, Gui.GetFontSize(),
                                            Gui.GetStyle().FontScaleDpi, messageWidth, true, NativeWidgetWeight, Gui.GetColorU32(ImGuiCol.Text));
                                        Gui.Dummy(new Vector2(messageWidth, row.MessageHeight));
                                        PaintNativeWidget(layout, textOrigin, textOrigin, textOrigin + new Vector2(messageWidth, row.MessageHeight));
                                    }
                                    else
                                    {
                                    Gui.PushTextWrapPos(Gui.GetCursorPosX() + messageWidth);
                                    try { Gui.TextUnformatted(row.Entry.MessageText ?? row.Entry.Text); }
                                    finally { Gui.PopTextWrapPos(); }
                                    }
                                }
                            if (Gui.TableNextColumn()) DrawNativeLabel(row.Entry.AssetUrl ?? row.Asset.Name);
                            }
                            finally { Gui.PopID(); }
                        }
                        drawnThrough = end;
                    }
                }
                finally { Gui.Destroy(clipper); }
            }
            finally { Gui.EndTable(); }
        }
        if (selectedAssetError is { } selected)
        {
            using var details = ImGuiScopes.Child("Asset error detail", new Vector2(0, 0), ImGuiChildFlags.Borders, ImGuiWindowFlags.None);
            if (details.DrawContents)
            {
                DrawSelectableText("##AssetErrorMessage", selected.Entry.MessageText ?? selected.Entry.Text);
                if (assetErrorStackTrace && selected.Entry.Exception is { } exception) DrawSelectableText("##AssetErrorException", exception);
                DrawPrefabConflictActions(selected.Asset.Id);
            }
        }
        return navigate;
    }

    private void DrawAssetErrorToolbar()
    {
        using var padding = ImGuiScopes.Style(ImGuiStyleVar.FramePadding, new Vector2(UiScale(3), UiScale(2)));
        Gui.AlignTextToFramePadding(); Gui.TextUnformatted("Filters:");
        DrawAssetErrorLevel(LogMessageType.Debug, ref assetErrorDebug);
        DrawAssetErrorLevel(LogMessageType.Verbose, ref assetErrorVerbose);
        DrawAssetErrorLevel(LogMessageType.Info, ref assetErrorInfo);
        DrawAssetErrorLevel(LogMessageType.Warning, ref assetErrorWarnings);
        DrawAssetErrorLevel(LogMessageType.Error, ref assetErrorErrors);
        DrawAssetErrorLevel(LogMessageType.Fatal, ref assetErrorFatal);
        AssetErrorToolbarSameLine(UiScale(22));
        Gui.PushStyleColor(ImGuiCol.Button, Gui.GetStyle().Colors[(int)(assetErrorStackTrace ? ImGuiCol.Header : ImGuiCol.WindowBg)]);
        try { if (Gui.Button("##AssetErrorStacktrace", new Vector2(UiScale(22), Gui.GetFrameHeight()))) assetErrorStackTrace = !assetErrorStackTrace; }
        finally { Gui.PopStyleColor(); }
        var stacktraceSize = UiScale(10);
        var stacktraceWidth = Gui.CalcTextSize("(...)").X * stacktraceSize / Gui.GetFontSize();
        var stacktraceCenter = (Gui.GetItemRectMin() + Gui.GetItemRectMax()) * .5f;
        Gui.AddText(Gui.GetWindowDrawList(), Gui.GetFont(), stacktraceSize,
            stacktraceCenter - new Vector2(stacktraceWidth * .5f, stacktraceSize * .5f), Gui.GetColorU32(ImGuiCol.Text), "(...)", 0);
        if (Gui.IsItemHovered()) Gui.SetTooltip(EditorLocalization.Text("Toggle Exception Stack Trace", "ToolTip"));
    }

    private void UpdateAssetErrorLayout(float width)
    {
        var font = Gui.GetFontSize(); var padding = Gui.GetStyle().CellPadding.Y;
        var dpi = Gui.GetStyle().FontScaleDpi; var native = CanDrawNativeWidgetText;
        if (!assetErrorLayoutDirty && assetErrorLayoutWidth == width && assetErrorLayoutFont == font && assetErrorLayoutPadding == padding
            && assetErrorLayoutDpi == dpi && assetErrorLayoutNative == native) return;
        assetErrorLayoutDirty = true;
        assetErrorLayoutWidth = width; assetErrorLayoutFont = font; assetErrorLayoutPadding = padding;
        assetErrorLayoutDpi = dpi; assetErrorLayoutNative = native;
        if (assetErrorRowOffsets.Length != visibleAssetErrorRows.Count + 1) assetErrorRowOffsets = new int[visibleAssetErrorRows.Count + 1];
        assetErrorRowOffsets[0] = 0;
        for (var index = 0; index < visibleAssetErrorRows.Count; index++)
        {
            var row = visibleAssetErrorRows[index];
            var text = row.Entry.MessageText ?? row.Entry.Text;
            var textHeight = native ? (nativeWidgetText ??= new()).Measure(text, font, dpi, NativeWidgetWeight, width: width, wrap: true).Y
                : Gui.CalcTextSize(text, false, width).Y;
            if (native)
                textHeight = Math.Max(textHeight, nativeWidgetText!.Measure(row.Entry.AssetUrl ?? row.Asset.Name, font, dpi, NativeWidgetWeight).Y);
            row.MessageHeight = Math.Max(Gui.GetTextLineHeight(), textHeight);
            var extent = (int)Math.Ceiling(row.MessageHeight + padding * 2);
            assetErrorRowOffsets[index + 1] = checked(assetErrorRowOffsets[index] + extent);
        }
        assetErrorLayoutDirty = false;
    }
    private int FindAssetErrorRow(int pixel)
    {
        var first = 0; var end = visibleAssetErrorRows.Count;
        while (first < end)
        {
            var middle = first + (end - first) / 2;
            if (assetErrorRowOffsets[middle + 1] <= pixel) first = middle + 1;
            else end = middle;
        }
        return first;
    }

    private void DrawAssetErrorLevel(LogMessageType level, ref bool visible)
    {
        var size = Gui.GetFrameHeight();
        AssetErrorToolbarSameLine(size);
        Gui.PushID((int)level);
        try
        {
        Gui.PushStyleColor(ImGuiCol.Button, Gui.GetStyle().Colors[(int)(visible ? ImGuiCol.Header : ImGuiCol.WindowBg)]);
        try { if (Gui.Button("##AssetErrorLevel", new Vector2(size))) { visible = !visible; assetErrorFiltersDirty = true; } }
        finally { Gui.PopStyleColor(); }
        var origin = Gui.GetItemRectMin() + new Vector2((size - UiScale(12)) * .5f);
        DrawAssetErrorSeverity(origin, level, true);
        if (Gui.IsItemHovered()) Gui.SetTooltip(EditorLocalization.Text("Toggle " + level, "ToolTip"));
        }
        finally { Gui.PopID(); }
    }
    private static void AssetErrorToolbarSameLine(float width)
    {
        var right = Gui.GetCursorScreenPos().X + Gui.GetContentRegionAvail().X;
        if (Gui.GetItemRectMax().X + Gui.GetStyle().ItemSpacing.X + width <= right) Gui.SameLine();
    }
    private void DrawAssetErrorSeverity(Vector2 origin, LogMessageType level, bool colored = false)
    {
        var list = Gui.GetWindowDrawList(); var size = UiScale(12);
        var color = colored ? Gui.GetColorU32(level switch
        {
            LogMessageType.Debug => new System.Numerics.Vector4(169 / 255f, 169 / 255f, 169 / 255f, 1),
            LogMessageType.Verbose => System.Numerics.Vector4.One,
            LogMessageType.Info => new System.Numerics.Vector4(144 / 255f, 238 / 255f, 144 / 255f, 1),
            LogMessageType.Warning => new System.Numerics.Vector4(1, 215 / 255f, 0, 1),
            LogMessageType.Error => new System.Numerics.Vector4(1, 99 / 255f, 71 / 255f, 1),
            _ => new System.Numerics.Vector4(1, 0, 0, 1),
        }) : Gui.GetColorU32(ImGuiCol.Text);
        var innerColor = colored ? Gui.GetColorU32(new System.Numerics.Vector4(.1f, .1f, .1f, 1)) : color;
        var center = origin + new Vector2(size * .5f);
        if (level == LogMessageType.Debug)
        {
            list.AddCircleFilled(center, size * .25f, color);
            list.AddCircleFilled(center - new Vector2(0, size * .3f), size * .12f, color);
            for (var index = -1; index <= 1; index++)
                list.AddLine(center + new Vector2(-size * .45f, index * size * .2f), center + new Vector2(size * .45f, index * size * .2f), color, UiScale(1));
        }
        else if (level == LogMessageType.Verbose)
        {
            list.AddCircleFilled(center - new Vector2(0, size * .08f), size * .35f, color);
            list.AddTriangleFilled(center, origin + new Vector2(size * .2f, size), center + new Vector2(size * .15f, 0), color);
        }
        else if (level == LogMessageType.Info)
        {
            var infoMark = Gui.GetColorU32(ImGuiCol.WindowBg);
            list.AddCircleFilled(center, size * .45f, color);
            list.AddLine(center - new Vector2(0, size * .05f), center + new Vector2(0, size * .25f), infoMark, UiScale(1.5f));
            list.AddCircleFilled(center - new Vector2(0, size * .23f), UiScale(.75f), infoMark);
        }
        else if (level == LogMessageType.Warning)
        {
            if (colored) list.AddTriangleFilled(origin + new Vector2(size * .5f, 0), origin + new Vector2(size, size), origin + new Vector2(0, size), color);
            else list.AddTriangle(origin + new Vector2(size * .5f, 0), origin + new Vector2(size, size), origin + new Vector2(0, size), color, UiScale(1.5f));
            list.AddLine(center - new Vector2(0, size * .2f), center + new Vector2(0, size * .15f), innerColor, UiScale(1.5f));
            list.AddCircleFilled(center + new Vector2(0, size * .32f), UiScale(.75f), innerColor);
        }
        else
        {
            if (colored) list.AddNgonFilled(center, size * .45f, color, level == LogMessageType.Fatal ? 8 : 16);
            else list.AddCircle(center, size * .45f, color, level == LogMessageType.Fatal ? 8 : 16, UiScale(1.5f));
            if (level == LogMessageType.Fatal && colored)
            {
                var textSize = UiScale(3.5f); var textWidth = Gui.CalcTextSize("STOP").X * textSize / Gui.GetFontSize();
                Gui.AddText(list, Gui.GetFont(), textSize, center - new Vector2(textWidth * .5f, textSize * .5f), innerColor, "STOP", 0);
            }
            else list.AddLine(center - new Vector2(size * .25f, 0), center + new Vector2(size * .25f, 0), innerColor, UiScale(1.5f));
        }
    }

    private void RefreshAssetErrorRows(IAssetTagDocument[] retained)
    {
        assetErrorWorkspace = workspace; assetErrorSelectionVersion = assetBrowser.SelectionVersion;
        assetErrorDocumentRevisions.Clear();
        foreach (var document in retained) assetErrorDocumentRevisions[document] = document.EditRevision;
        var previous = selectedAssetError?.Entry;
        assetErrorRows.Clear(); selectedAssetError = null; assetErrorFiltersDirty = true;
        if (workspace == null) return;
        var authored = retained.ToDictionary(document => document.AuthoredAsset.Id, document => document.AuthoredAsset);
        var dependencies = workspace.Session.DependencyManager.CaptureDependencies(authored);
        var selected = assetBrowser.SelectedEntries.Select(asset => asset.Id).ToHashSet();
        var queue = new Queue<AssetId>(selected);
        while (queue.TryDequeue(out var id))
            if (dependencies.TryGetValue(id, out var dependency))
                foreach (var link in dependency.LinksOut)
                    if ((link.Type & ContentLinkType.All) != 0 && selected.Add(link.Item.Id)) queue.Enqueue(link.Item.Id);
        var byId = workspace.AssetItems.ToDictionary(item => item.Id, item => new AssetEntry(item.FullPath.ToOSPath(), item.UnqualifiedUrl.ToString(),
            System.IO.Path.GetExtension(item.FullPath.ToOSPath()), item.Asset.GetType(), item.Id, item.Asset.Tags.ToArray()));
        var byPath = byId.Values.GroupBy(asset => asset.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var entry in assetErrorEntries)
        {
            var asset = entry.AssetId is { } assetId ? byId.GetValueOrDefault(assetId)
                : entry.Diagnostic is { } diagnostic ? byPath.GetValueOrDefault(diagnostic.Path) : null;
            if (asset != null && selected.Contains(asset.Id))
            {
                var row = new AssetErrorRow(entry, asset); assetErrorRows.Add(row);
                if (ReferenceEquals(entry, previous)) selectedAssetError = row;
            }
        }
    }
}
