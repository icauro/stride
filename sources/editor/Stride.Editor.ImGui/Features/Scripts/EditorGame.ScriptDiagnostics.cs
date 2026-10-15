using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    /// <summary>Activates retained source without discarding its unsaved text and positions its caret.</summary>
    private bool NavigateScriptDiagnostic(BuildDiagnostic diagnostic)
    {
        if (!Path.GetExtension(diagnostic.Path).Equals(".cs", StringComparison.OrdinalIgnoreCase)) return false;
        var path = Path.GetFullPath(diagnostic.Path);
        var entry = documents.Find(path);
        if (entry == null)
        {
            var item = workspace?.AssetItems.FirstOrDefault(candidate =>
                string.Equals(Path.GetFullPath(candidate.FullPath.ToOSPath()), path, StringComparison.OrdinalIgnoreCase));
            var source = GetCanonicalDocument(path, () => item?.Asset is SourceCodeAsset ? new AssetScriptSourceDocument(item, sessionHistory) : CreateReadonlyProjectSourceDocument(path));
            entry = documents.Add(source);
            try { ActivateDocument(entry); }
            catch { documents.Remove(entry); throw; }
        }
        else ActivateDocument(entry);
        if (entry.Document is not ScriptSourceDocument document) return false;
        if (!scriptEditors.TryGetValue(document, out var editor))
            scriptEditors.Add(document, editor = new ScriptTextEditor());
        editor.Navigate(diagnostic.Line, diagnostic.Column);
        return true;
    }
}
