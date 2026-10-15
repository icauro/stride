using System.Numerics;
using Hexa.NET.ImGui;
using Microsoft.CodeAnalysis.Text;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed unsafe partial class ScriptTextEditor
{
    private sealed record SourcePreview(string Path, ScriptSourceChangeKind Kind, string[] Original, string[] Modified, HashSet<int> Removed, HashSet<int> Added,
        string? DestinationPath = null);
    private ScriptAssistResult? codeActionPreview, confirmedCodeAction;
    private SourcePreview[] previewFiles = [];
    private int selectedPreviewFile;
    private bool openCodeActionPreview;
    private string CodeActionPreviewTitle => "Preview Changes###ScriptCodeActionPreview" + label;

    private void ClearCodeActionPreview()
    {
        ClearPreviewSourceGeometry();
        codeActionPreview?.CodeActionBatch?.Dispose(); confirmedCodeAction?.CodeActionBatch?.Dispose();
        codeActionPreview = null; confirmedCodeAction = null; previewFiles = []; openCodeActionPreview = false;
    }
    private void SetCodeActionPreview(ScriptAssistResult result)
    {
        ClearPreviewSourceGeometry();
        codeActionPreview?.CodeActionBatch?.Dispose();
        codeActionPreview = result; selectedPreviewFile = 0; openCodeActionPreview = true;
        completion = null; signatures = []; definitions = []; codeActions = [];
        var previewEdits = result.CodeActionBatch!.Edits.Concat((result.CodeActionBatch.ProjectEdits ?? []).Select(edit =>
            new ScriptCodeActionSourceEdit(edit.Path, edit.Before,
                [new TextChange(new TextSpan(0, edit.Before.Length), edit.After)], null, null)));
        var sources = previewEdits.Select(edit =>
        {
            var original = SourceText.From(edit.Before);
            var modified = original.WithChanges(edit.Changes);
            var removed = new HashSet<int>(); var added = new HashSet<int>(); var delta = 0;
            foreach (var change in edit.Changes.OrderBy(change => change.Span.Start))
            {
                if (change.Span.Length > 0)
                    for (var line = original.Lines.GetLineFromPosition(change.Span.Start).LineNumber;
                        line <= original.Lines.GetLineFromPosition(change.Span.End - 1).LineNumber; line++) removed.Add(line);
                var start = change.Span.Start + delta;
                var end = start + (change.NewText?.Length ?? 0);
                if (end > start)
                    for (var line = modified.Lines.GetLineFromPosition(start).LineNumber;
                        line <= modified.Lines.GetLineFromPosition(end - 1).LineNumber; line++) added.Add(line);
                delta += (change.NewText?.Length ?? 0) - change.Span.Length;
            }
            return new SourcePreview(edit.Path, edit.Kind, original.Lines.Select(line => line.ToString()).ToArray(),
                modified.Lines.Select(line => line.ToString()).ToArray(), removed, added, edit.DestinationPath);
        });
        var contexts = (result.CodeActionBatch.NativeContexts ?? []).Where(context => !context.BeforeMetadata.SequenceEqual(context.Metadata)
            || !context.BeforeAnalyzers.SequenceEqual(context.Analyzers)).Select(context =>
        {
            var before = context.BeforeMetadata.Select(ScriptNativeReferenceContext.Describe).Concat(context.BeforeAnalyzers.Select(ScriptNativeReferenceContext.Describe)).ToArray();
            var after = context.Metadata.Select(ScriptNativeReferenceContext.Describe).Concat(context.Analyzers.Select(ScriptNativeReferenceContext.Describe)).ToArray();
            var removed = context.BeforeMetadata.Select((reference, index) => (reference, index)).Where(value => !context.Metadata.Contains(value.reference)).Select(value => value.index)
                .Concat(context.BeforeAnalyzers.Select((reference, index) => (reference, index)).Where(value => !context.Analyzers.Contains(value.reference)).Select(value => value.index + context.BeforeMetadata.Length)).ToHashSet();
            var added = context.Metadata.Select((reference, index) => (reference, index)).Where(value => !context.BeforeMetadata.Contains(value.reference)).Select(value => value.index)
                .Concat(context.Analyzers.Select((reference, index) => (reference, index)).Where(value => !context.BeforeAnalyzers.Contains(value.reference)).Select(value => value.index + context.Metadata.Length)).ToHashSet();
            return new SourcePreview((context.ProjectPath ?? context.ProjectName) + " (References)", ScriptSourceChangeKind.Modified, before, after, removed, added);
        });
        var information = (result.CodeActionBatch.NativeContexts ?? []).SelectMany(context => context.Documents.Select((document, index) =>
        {
            var before = context.BeforeDocuments[index];
            if (document.Same(before)) return null;
            var original = before.Describe(); var modified = document.Describe();
            var changed = Enumerable.Range(0, original.Length).Where(row => original[row] != modified[row]).ToHashSet();
            return new SourcePreview((context.ProjectPath ?? context.ProjectName) + "/" + document.Name,
                ScriptSourceChangeKind.Modified, original, modified, changed, new HashSet<int>(changed));
        }).OfType<SourcePreview>());
        previewFiles = sources.Concat(contexts).Concat(information).ToArray();
    }
    public ScriptAssistResult? TakeConfirmedCodeAction()
    {
        var result = confirmedCodeAction; confirmedCodeAction = null; return result;
    }
    public void ApplyConfirmedCodeAction(ScriptAssistResult result, ScriptSourceDocument document, Action<ScriptCodeActionBatch> apply)
    {
        if (!MayEditSource(document) || result.Source.Version != document.Version || !string.Equals(result.Source.Path, document.Path, StringComparison.OrdinalIgnoreCase)
            || result.Request.Generation != AssistanceGeneration || result.Request.Position != caretPosition
            || result.Request.Selection is { } requested && requested != selection)
            throw new InvalidOperationException("The code-action source or selection changed.");
        var batch = result.CodeActionBatch ?? throw new InvalidOperationException("The code-action preview expired.");
        var position = caretPosition;
        var edit = batch.Edits.FirstOrDefault(value => string.Equals(value.Path, document.Path, StringComparison.OrdinalIgnoreCase));
        if (edit != null)
        {
            var delta = 0;
            foreach (var change in edit.Changes.OrderBy(change => change.Span.Start))
            {
                if (caretPosition < change.Span.Start) break;
                if (caretPosition <= change.Span.End) { position = change.Span.Start + delta + (change.NewText?.Length ?? 0); break; }
                delta += (change.NewText?.Length ?? 0) - change.Span.Length;
                position = caretPosition + delta;
            }
            var changed = SourceText.From(edit.Before).WithChanges(edit.Changes).ToString();
            position = changed[..Math.Clamp(position, 0, changed.Length)].Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Length;
        }
        try { apply(batch); }
        finally { batch.Dispose(); }
        DismissAssistance(); NavigatePosition(Math.Clamp(position, 0, document.EditorText.Length), document.EditorText);
    }

    private void DrawCodeActionPreview(ScriptSourceDocument document)
    {
        if (openCodeActionPreview) { Gui.OpenPopup(CodeActionPreviewTitle); openCodeActionPreview = false; }
        if (!Gui.IsPopupOpen(CodeActionPreviewTitle)) return;
        Gui.SetNextWindowSize(new Vector2(Math.Min(1100, Gui.GetIO().DisplaySize.X * 0.9f), Math.Min(700, Gui.GetIO().DisplaySize.Y * 0.85f)), ImGuiCond.FirstUseEver);
        if (!Gui.BeginPopupModal(CodeActionPreviewTitle, ImGuiWindowFlags.NoDocking)) return;
        if (codeActionPreview is not { } result || result.Source.Version != document.Version
            || result.Request.Generation != AssistanceGeneration || result.Request.Position != caretPosition
            || result.Request.Selection is { } requested && requested != selection)
        {
            ClearCodeActionPreview(); Gui.CloseCurrentPopup(); Gui.EndPopup(); return;
        }
        Gui.TextUnformatted(result.CodeActionBatch!.Title);
        var bodyHeight = Math.Max(80, Gui.GetContentRegionAvail().Y - Gui.GetFrameHeightWithSpacing() * 2);
        if (Gui.BeginChild("##Changed source files", new Vector2(210, bodyHeight), ImGuiChildFlags.Borders))
            for (var index = 0; index < previewFiles.Length; index++)
            {
                Gui.PushID(index);
                var file = previewFiles[index];
                var prefix = file.Kind == ScriptSourceChangeKind.Added ? "+ " : file.Kind == ScriptSourceChangeKind.Removed ? "− " : "";
                var name = Path.GetFileName(file.Path) + (file.DestinationPath == null ? "" : " → " + Path.GetFileName(file.DestinationPath));
                if (Gui.Selectable(prefix + name, index == selectedPreviewFile)) selectedPreviewFile = index;
                if (Gui.IsItemHovered()) Gui.SetTooltip(previewFiles[index].Path);
                Gui.PopID();
            }
        ImGuiScopes.EndChild(); Gui.SameLine();
        if (Gui.BeginChild("##Source changes", new Vector2(0, bodyHeight)))
            if (previewFiles.Length > 0)
            {
                var file = previewFiles[selectedPreviewFile]; Gui.TextUnformatted(file.DestinationPath == null ? file.Path : file.Path + " → " + file.DestinationPath);
                var paneWidth = Math.Max(60, (Gui.GetContentRegionAvail().X - Gui.GetStyle().ItemSpacing.X) / 2);
                DrawPreviewSource("Original", file.Original, file.Removed, new Vector4(1, 0.5f, 0.5f, 1), new Vector2(paneWidth, 0));
                Gui.SameLine();
                DrawPreviewSource("Modified", file.Modified, file.Added, new Vector4(0.5f, 1, 0.6f, 1), new Vector2(0, 0));
            }
        ImGuiScopes.EndChild();
        Gui.BeginDisabled(!MayEditSource(document));
        if (Gui.Button("Apply")) { confirmedCodeAction = result; codeActionPreview = null; previewFiles = []; ClearPreviewSourceGeometry(); Gui.CloseCurrentPopup(); }
        Gui.EndDisabled();
        Gui.SameLine();
        if (Gui.Button(EditorLocalization.Widget("Cancel", "Button")) || Gui.IsKeyPressed(ImGuiKey.Escape, false)) { ClearCodeActionPreview(); Gui.CloseCurrentPopup(); }
        Gui.EndPopup();
    }
    private void DrawPreviewSource(string label, string[] text, HashSet<int> changed, Vector4 color, Vector2 size)
    {
        if (Gui.BeginChild(label, size, ImGuiChildFlags.Borders, ImGuiWindowFlags.HorizontalScrollbar))
        {
            Gui.TextUnformatted(label);
            EnsurePreviewSourceGeometry();
            var rowHeight = nativeSourceHeight + Gui.GetStyle().ItemSpacing.Y;
            var gutterWidth = Gui.CalcTextSize(text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)).X + Gui.CalcTextSize("  ").X;
            var sourceTop = Gui.GetCursorPosY();
            var first = Math.Clamp((int)((Gui.GetScrollY() - sourceTop) / rowHeight) - 1, 0, text.Length);
            var last = Math.Min(text.Length, first + (int)(Gui.GetContentRegionAvail().Y / rowHeight) + 3);
            if (first > 0) Gui.Dummy(new Vector2(1, first * rowHeight - Gui.GetStyle().ItemSpacing.Y));
            for (var line = first; line < last; line++)
            {
                if (changed.Contains(line)) Gui.PushStyleColor(ImGuiCol.Text, color);
                var point = Gui.GetCursorScreenPos();
                var entry = PreviewSourceGeometry(text, line, Gui.GetColorU32(ImGuiCol.Text), Gui.GetColorU32(ImGuiCol.Text));
                var window = ImGuiP.GetCurrentWindow(); var draw = window.DrawList;
                draw.PushClipRect(window.InnerClipRect.Min, window.InnerClipRect.Max, true);
                draw.AddText(point + new Vector2(gutterWidth - Gui.CalcTextSize(entry.Number).X - Gui.CalcTextSize("  ").X, 0), Gui.GetColorU32(ImGuiCol.Text), entry.Number);
                entry.Geometry.Native.Draw(draw, NativeGlyphCommands!, NativeGlyphImages!, point + new Vector2(gutterWidth, 0), Gui.GetColorU32(ImGuiCol.Text), window.InnerClipRect.Min, window.InnerClipRect.Max);
                draw.PopClipRect();
                Gui.Dummy(new Vector2(Math.Max(1, gutterWidth + entry.Geometry.Width), nativeSourceHeight));
                if (changed.Contains(line)) Gui.PopStyleColor();
            }
            if (last < text.Length) Gui.Dummy(new Vector2(1, (text.Length - last) * rowHeight - Gui.GetStyle().ItemSpacing.Y));
        }
        ImGuiScopes.EndChild();
    }
}
