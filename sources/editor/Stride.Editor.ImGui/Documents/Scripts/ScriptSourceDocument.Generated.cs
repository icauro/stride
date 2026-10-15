using System.Security.Cryptography;
using System.Text;
using Stride.Core.Assets;
using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

internal partial class ScriptSourceDocument
{
    internal AssetId? GeneratedByAssetId { get; private set; }
    private EditorAuthoringState? generatedProducer;
    private IDisposable? generatedProducerLease;
    internal Action<ScriptSourceDocument>? VerifyGeneratedOutput { get; set; }
    internal ScriptSourceDocument(string path, AssetId generatedBy, string projectPath, EditorSessionHistory history)
    {
        Path = System.IO.Path.GetFullPath(path);
        if (File.Exists(Path) || Directory.Exists(Path))
            throw new InvalidOperationException("The generated source requires a free path in its actual asset directory.");
        historyScope = new(this, history, "Generate visual script", 2048);
        encoding = new UTF8Encoding(false, true); preamble = [];
        Text = EditorText = savedText = ""; newline = "\r\n";
        IsCSharpSource = true; WasCreatedInSession = true; sourceCanSave = true;
        GeneratedByAssetId = generatedBy; SetProjectOwnership(projectPath);
        savedCodePage = encoding.CodePage;
    }
    internal void BindGeneratedOutput(AssetId generatedBy, string projectPath, bool readOnly, EditorAuthoringState producer)
    {
        if (asset != null || !IsCSharpSource || GeneratedByAssetId is { } previous && previous != generatedBy)
            throw new InvalidOperationException("The declared generated path already belongs to another source asset.");
        if (GeneratedByAssetId == null && IsDirty)
            throw new InvalidOperationException("The existing source has pending authored changes and cannot be adopted as compiler output.");
        if (producer.AssetId != generatedBy || !ReferenceEquals(producer.Registry.Find(generatedBy), producer))
            throw new InvalidOperationException("The generated source has no current canonical producer.");
        if (!ReferenceEquals(generatedProducer, producer))
        {
            var lease = producer.AcquireHistory();
            generatedProducerLease?.Dispose(); generatedProducerLease = lease; generatedProducer = producer;
        }
        GeneratedByAssetId = generatedBy; SetProjectOwnership(projectPath);
        sourceCanSave = !readOnly && (!File.Exists(Path) || (File.GetAttributes(Path) & FileAttributes.ReadOnly) == 0);
    }
    internal void UpdateGeneratedText(AssetId producer, string actualSource, EditorAuthoringState producerState)
    {
        if (GeneratedByAssetId != producer || producerState.AssetId != producer || !CanSave
            || !ReferenceEquals(producerState.Registry.Find(producer), producerState))
            throw new InvalidOperationException("The compiler output has no current canonical visual-script owner.");
        ValidateGeneratedFileBaseline();
        var utf8 = new UTF8Encoding(false, true); utf8.GetByteCount(actualSource);
        if (Text == actualSource && encoding.CodePage == utf8.CodePage && preamble.Length == 0) return;
        var beforeText = Text; var beforeEditorText = EditorText; var beforeNewline = newline;
        var afterEditorText = Text == actualSource ? EditorText : Normalize(actualSource);
        var afterNewline = actualSource.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var beforeEncoding = encoding; var beforePreamble = preamble;
        var beforeRevision = revision; var afterRevision = ++nextRevision;
        void Assign(bool forward)
        {
            // Derived projection refreshes may run between history operations.
            // Restore the recorded compiler state independently of current text.
            Text = forward ? actualSource : beforeText;
            EditorText = forward ? afterEditorText : beforeEditorText;
            encoding = forward ? utf8 : beforeEncoding; preamble = forward ? [] : beforePreamble;
            newline = forward ? afterNewline : beforeNewline;
            revision = forward ? afterRevision : beforeRevision; Version++;
        }
        using var transaction = historyScope.CreateTransaction("Generate visual script");
        Assign(true);
        try { historyScope.Record(new GeneratedTextOperation(Assign), producerState); }
        catch { Assign(false); throw; }
    }
    internal void RefreshGeneratedProjection(string actualSource, EditorAuthoringState producerState)
    {
        if (!IsGeneratedOutputOwnedBy(producerState))
            throw new InvalidOperationException("The generated projection has no current canonical producer.");
        ValidateGeneratedFileBaseline();
        encoding.GetByteCount(actualSource);
        if (Text == actualSource) return;
        Text = actualSource; EditorText = Normalize(actualSource);
        newline = Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        revision = ++nextRevision; Version++;
    }
    internal Action ApplyDerivedGeneratedProjection(AssetId producerId, string projectPath, bool readOnly,
        EditorAuthoringState producerState, string actualSource)
    {
        ValidateGeneratedFileBaseline();
        encoding.GetByteCount(actualSource);
        var nextEditorText = Normalize(actualSource);
        var nextNewline = actualSource.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var previousProducer = generatedProducer ?? throw new InvalidOperationException("The generated projection has no retained producer.");
        var previousProject = ProjectPath!;
        var previousCanSave = sourceCanSave;
        var previousText = Text; var previousEditorText = EditorText; var previousNewline = newline;
        var previousRevision = revision; var previousNext = nextRevision; var previousVersion = Version;
        void Restore()
        {
            BindGeneratedOutput(producerId, previousProject, !previousCanSave, previousProducer);
            sourceCanSave = previousCanSave;
            Text = previousText; EditorText = previousEditorText; newline = previousNewline;
            revision = previousRevision; nextRevision = previousNext; Version = previousVersion;
        }
        BindGeneratedOutput(producerId, projectPath, readOnly, producerState);
        if (Text != actualSource)
        {
            Text = actualSource; EditorText = nextEditorText; newline = nextNewline;
            revision = ++nextRevision; Version++;
        }
        return Restore;
    }
    internal void ValidateGeneratedFileBaseline()
    {
        if (File.Exists(Path))
        {
            using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (diskHash == null || !SHA256.HashData(stream).AsSpan().SequenceEqual(diskHash))
                throw new IOException("The generated source changed outside its canonical owner.");
        }
        else if (diskHash != null || Directory.Exists(Path)) throw new IOException("The generated source baseline is no longer present.");
    }
    private sealed class GeneratedTextOperation(Action<bool> assign) : Operation
    {
        protected override void Rollback() => assign(false);
        protected override void Rollforward() => assign(true);
    }
    private void ReleaseGeneratedOutput()
    {
        generatedProducerLease?.Dispose(); generatedProducerLease = null; generatedProducer = null; VerifyGeneratedOutput = null;
    }
    internal void ReleaseGeneratedOutputOwnership() => ReleaseGeneratedOutput();
    internal bool IsGeneratedOutputOwnedBy(EditorAuthoringState producer)
        => GeneratedByAssetId == producer.AssetId && ReferenceEquals(generatedProducer, producer)
            && generatedProducerLease != null && producer.IsPublished && !producer.IsDisposed
            && producer.AssetId is { } id && ReferenceEquals(producer.Registry.Find(id), producer);
}
