using System.Security.Cryptography;
using System.Text;
using Stride.Core.Assets;
using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

/// <summary>Actual project source, preserving its encoding and unchanged newline sequences.</summary>
internal partial class ScriptSourceDocument : IEditorDocument, IRelocatableEditorDocument
{
    private readonly SourceCodeAsset? asset;
    private AssetItem? sourceItem;
    private Encoding encoding;
    private byte[] preamble;
    private byte[]? diskHash;
    private readonly EditorDocumentHistoryScope historyScope;
    private long revision, savedRevision, nextRevision;
    private string? savedText;
    private int savedCodePage;
    private byte[] savedPreamble = [];
    private bool sourceCanSave;
    private string newline;
    public string Path { get; private set; }
    public string Text { get; private set; }
    public string EditorText { get; private set; }
    public long Version { get; private set; }
    public bool CanSave => sourceCanSave;
    public bool CanEditText => sourceCanSave && GeneratedByAssetId == null;
    public virtual bool IsDirty => (GeneratedByAssetId == null || sourceCanSave) && (diskHash == null || revision != savedRevision
        && (Text != savedText || encoding.CodePage != savedCodePage || !preamble.AsSpan().SequenceEqual(savedPreamble)));
    public bool CanUndo => historyScope.Stack.CanRollback;
    public bool CanRedo => historyScope.Stack.CanRollforward;

    public bool IsCSharpSource { get; }
    internal string? ProjectPath { get; private set; }
    public ScriptSourceDocument(AssetItem item, EditorSessionHistory? history = null) : this(item.FullPath.ToOSPath(),
        item.Asset as SourceCodeAsset ?? throw new ArgumentException("The asset is not a source document.", nameof(item)), item.Package.IsReadOnly, history)
    { sourceItem = item; ProjectPath = (item.Package.Container as SolutionProject)?.FullPath.ToOSPath(); }
    public ScriptSourceDocument(string path, bool readOnly = false, EditorSessionHistory? history = null) : this(path, null, readOnly, history) { }
    private ScriptSourceDocument(string path, SourceCodeAsset? sourceAsset, bool readOnly, EditorSessionHistory? history)
    {
        historyScope = new(this, history, "Edit source", 2048);
        asset = sourceAsset;
        Path = System.IO.Path.GetFullPath(path);
        IsCSharpSource = System.IO.Path.GetExtension(Path).Equals(".cs", StringComparison.OrdinalIgnoreCase);
        sourceCanSave = !readOnly && (File.GetAttributes(Path) & FileAttributes.ReadOnly) == 0;
        var bytes = File.ReadAllBytes(Path);
        (encoding, preamble) = DetectEncoding(bytes);
        Text = encoding.GetString(bytes, preamble.Length, bytes.Length - preamble.Length);
        savedText = Text;
        savedCodePage = encoding.CodePage; savedPreamble = preamble.ToArray();
        EditorText = Normalize(Text);
        newline = Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : Text.Contains('\r') ? "\r" : "\n";
        diskHash = SHA256.HashData(bytes);
    }

    public void UpdateEditorText(string text)
    {
        if (!CanEditText) throw new InvalidOperationException("This source file is read-only.");
        if (text == EditorText) return;
        var prefix = 0;
        while (prefix < text.Length && prefix < EditorText.Length && text[prefix] == EditorText[prefix]) prefix++;
        var suffix = 0;
        while (suffix < text.Length - prefix && suffix < EditorText.Length - prefix && text[^(suffix + 1)] == EditorText[^(suffix + 1)]) suffix++;
        UpdateEditorChanges([new(new(prefix, EditorText.Length - prefix - suffix), text.Substring(prefix, text.Length - prefix - suffix))]);
    }
    private void UpdateRawText(string text)
    {
        if (text == Text) return;
        encoding.GetByteCount(text);
        var start = 0;
        while (start < text.Length && start < Text.Length && text[start] == Text[start]) start++;
        var suffix = 0;
        while (suffix < text.Length - start && suffix < Text.Length - start && text[^(suffix + 1)] == Text[^(suffix + 1)]) suffix++;
        var end = Text.Length - suffix;
        var inserted = text.Substring(start, text.Length - start - suffix);
        var edit = new TextEdit(start, Text[start..end], inserted, revision, ++nextRevision);
        using var transaction = historyScope.CreateTransaction(edit.Removed.Length == 0 ? "Insert text" : edit.Inserted.Length == 0 ? "Delete text" : "Replace text");
        Apply(edit.Start, edit.Removed.Length, edit.Inserted, edit.After);
        try { historyScope.Record(new TextEditOperation(this, edit)); }
        catch { Apply(edit.Start, edit.Inserted.Length, edit.Removed, edit.Before); throw; }
    }

    private int RawOffset(int normalizedOffset)
    {
        var raw = 0;
        for (var index = 0; index < normalizedOffset; index++)
        {
            if (Text[raw++] == '\r' && raw < Text.Length && Text[raw] == '\n') raw++;
        }
        return raw;
    }
    private void Apply(int start, int length, string inserted, long targetRevision)
    {
        Text = Text.Remove(start, length).Insert(start, inserted);
        EditorText = Normalize(Text); revision = targetRevision; Version++;
    }
    public void Undo() { historyScope.BeforeReplay(); if (CanUndo) historyScope.Stack.Rollback(); }
    public void Redo() { historyScope.BeforeReplay(); if (CanRedo) historyScope.Stack.Rollforward(); }
    public void CommitEdits() { }
    public virtual void Save()
    {
        if (!CanSave) throw new InvalidOperationException("This source file is read-only.");
        if (GeneratedByAssetId != null) (VerifyGeneratedOutput ?? throw new InvalidOperationException("The generated source has no current compiler owner."))(this);
        if (!IsDirty) return;
        historyScope.EnsureMaySave();
        var content = encoding.GetBytes(Text);
        var bytes = new byte[preamble.Length + content.Length];
        preamble.CopyTo(bytes, 0); content.CopyTo(bytes, preamble.Length);
        SourceCodeAsset? serializedSource = null;
        if (asset != null && !IsCSharpSource)
        {
            serializedSource = AssetCloner.Clone(asset); serializedSource.Text = Text;
            using var stream = new MemoryStream();
            AssetFileSerializer.Save(stream, serializedSource, sourceItem?.YamlMetadata);
            bytes = stream.ToArray();
        }
        if (SessionHistory != null && !ReferenceEquals(SessionHistory.Registry.Find(Path)?.Document, this))
            throw new InvalidOperationException("The source is no longer its current canonical authored owner.");
        diskHash = WindowsOwnedAssetFileWriter.Write(Path, diskHash, bytes).ContentHash;
        if (serializedSource != null)
        {
            (encoding, preamble) = DetectEncoding(bytes);
            Text = encoding.GetString(bytes, preamble.Length, bytes.Length - preamble.Length); EditorText = Normalize(Text); Version++;
        }
        savedRevision = revision; savedText = Text;
        savedCodePage = encoding.CodePage; savedPreamble = preamble.ToArray();
        historyScope.Saved(diskHash);
        if (asset != null) asset.Text = Text;
        MarkSourceMetadataSaved();
        if (sourceItem != null) sourceItem.IsDirty = false;
        SessionHistory?.ScriptSourceSaved?.Invoke(this);
    }
    protected virtual void MarkSourceMetadataSaved() { }
    public void Dispose()
    {
        historyScope.EnsureMayDispose();
        ReleaseReferenceContexts();
        ReleaseGeneratedOutput();
        if (SessionHistory == null) historyScope.Stack.Clear();
    }
    private static string Normalize(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    private static (Encoding Encoding, byte[] Preamble) DetectEncoding(byte[] bytes)
    {
        Encoding[] supported = [new UTF32Encoding(false, true, true), new UTF32Encoding(true, true, true),
            new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true), new UTF8Encoding(true, true)];
        foreach (var candidate in supported)
        {
            var marker = candidate.GetPreamble();
            if (bytes.AsSpan().StartsWith(marker)) return (candidate, marker);
        }
        return (new UTF8Encoding(false, true), []);
    }
    private sealed record TextEdit(int Start, string Removed, string Inserted, long Before, long After);
    private sealed class TextEditOperation(ScriptSourceDocument document, TextEdit edit) : Operation
    {
        protected override void Rollback() => document.Apply(edit.Start, edit.Inserted.Length, edit.Removed, edit.Before);
        protected override void Rollforward() => document.Apply(edit.Start, edit.Removed.Length, edit.Inserted, edit.After);
    }
}
