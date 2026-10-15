using System.Security.Cryptography;
using Microsoft.CodeAnalysis.Text;

namespace Stride.Editor.ImGui;

internal partial class ScriptSourceDocument
{
    internal void ValidateCodeActionBaseline(ScriptCodeActionSourceEdit edit)
    {
        if (!CanEditText || !IsCSharpSource || File.Exists(Path) && (File.GetAttributes(Path) & FileAttributes.ReadOnly) != 0) throw new InvalidOperationException("This source file is read-only.");
        if (edit.Version is { } version && version != Version || Normalize(edit.Before) != EditorText)
            throw new InvalidOperationException("The code-action source changed.");
        var currentHash = File.Exists(Path) ? SHA256.HashData(File.ReadAllBytes(Path)) : null;
        if (Directory.Exists(Path) || (currentHash == null) != (edit.DiskHash == null) || (currentHash == null) != (diskHash == null)
            || currentHash != null && (!currentHash.AsSpan().SequenceEqual(edit.DiskHash) || !currentHash.AsSpan().SequenceEqual(diskHash)))
            throw new InvalidOperationException("The source file changed outside the editor.");
        var changed = SourceText.From(edit.Before).WithChanges(edit.Changes).ToString();
        encoding.GetByteCount(changed);
    }

    internal void ApplyCodeActionChanges(ScriptCodeActionSourceEdit edit)
    {
        // Descending source spans preserve the provider's offsets, including unopened CRLF files.
        var text = SourceText.From(edit.Before);
        foreach (var change in edit.Changes.Select((change, index) => (change, index))
            .OrderByDescending(entry => entry.change.Span.Start).ThenByDescending(entry => entry.index).Select(entry => entry.change))
        {
            text = text.WithChanges(change);
            UpdateEditorText(Normalize(text.ToString()));
        }
    }
}
