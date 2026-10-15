using System.Text;
using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal partial class ScriptSourceDocument
{
    internal ScriptSourceDocument(string path, SourceCodeAsset sourceAsset, string authoredText, Encoding sourceEncoding, EditorSessionHistory history)
    {
        Path = System.IO.Path.GetFullPath(path);
        if (!System.IO.Path.GetExtension(Path).Equals(AssetRegistry.GetDefaultExtension(sourceAsset.GetType()), StringComparison.OrdinalIgnoreCase)
            || File.Exists(Path) || Directory.Exists(Path))
            throw new InvalidOperationException("The new source requires its native extension and a free destination path.");
        encoding = (Encoding)sourceEncoding.Clone();
        encoding.EncoderFallback = EncoderFallback.ExceptionFallback;
        encoding.DecoderFallback = DecoderFallback.ExceptionFallback;
        encoding.GetByteCount(authoredText);
        preamble = encoding.GetPreamble();
        asset = sourceAsset;
        Text = authoredText; EditorText = Normalize(authoredText); savedText = "";
        newline = authoredText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : authoredText.Contains('\r') ? "\r" : "\n";
        IsCSharpSource = System.IO.Path.GetExtension(Path).Equals(".cs", StringComparison.OrdinalIgnoreCase); sourceCanSave = true;
        WasCreatedInSession = true;
        historyScope = new(this, history, "Edit source", 2048);
        savedCodePage = encoding.CodePage; savedPreamble = preamble.ToArray();
        sourceAsset.Text = authoredText;
    }

    internal Encoding CopySourceEncoding() => (Encoding)encoding.Clone();
    internal string SourceName { get; private set; } = "";
    internal bool WasCreatedInSession { get; }
    internal string[] SourceFolders { get; private set; } = [];
    internal void SetProjectOwnership(string projectPath, string? name = null, string[]? folders = null)
    { ProjectPath = System.IO.Path.GetFullPath(projectPath); SourceName = name ?? System.IO.Path.GetFileName(Path); SourceFolders = folders?.ToArray() ?? []; }
    internal void ClearProjectOwnership() { ProjectPath = null; SourceFolders = []; SourceName = System.IO.Path.GetFileName(Path); }
    ReadOnlyMemory<byte> IRelocatableEditorDocument.DiskContentHash => DiskContentHash;
    public void RelocatePath(string path)
    {
        Path = System.IO.Path.GetFullPath(path); SourceName = System.IO.Path.GetFileName(Path);
        if (ProjectPath != null) SourceFolders = System.IO.Path.GetRelativePath(System.IO.Path.GetDirectoryName(ProjectPath)!, System.IO.Path.GetDirectoryName(Path)!).Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Where(part => part != ".").ToArray();
        Version++;
    }
    internal ReadOnlyMemory<byte> DiskContentHash => diskHash ?? [];
    internal void BindSourceAssetItem(AssetItem item)
    {
        if (!ReferenceEquals(item.Asset, asset) || !string.Equals(System.IO.Path.GetFullPath(item.FullPath.ToOSPath()), Path, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The source item does not own this actual authored source.");
        sourceItem = item;
    }
}
