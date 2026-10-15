namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void OpenScriptDocument(string path, bool reload = false)
    {
        var entry = documents.Find(path);
        if (entry != null && !reload) { ActivateDocument(entry); return; }
        var next = GetCanonicalDocument(path, () => CreateScriptDocument(path));
        if (entry == null)
        {
            entry = documents.Add(next);
            try { ActivateDocument(entry); }
            catch { documents.Remove(entry); throw; }
        }
        else
        {
            ReplaceAuthoredDocument(entry, () => CreateScriptDocument(path), "Reload " + entry.Name);
        }
    }
    private ScriptSourceDocument CreateScriptDocument(string path)
    {
        var canonical = Path.GetFullPath(path);
        var asset = workspace?.AssetItems.FirstOrDefault(item => SamePath(item.FullPath.ToOSPath(), canonical));
        if (asset != null) return new AssetScriptSourceDocument(asset, sessionHistory);
        return CreateReadonlyProjectSourceDocument(canonical);
    }
    private ScriptSourceDocument CreateReadonlyProjectSourceDocument(string path)
    {
        var canonical = Path.GetFullPath(path);
        var source = new ScriptSourceDocument(canonical, true, sessionHistory);
        try
        {
            // Bind a read-only viewer once at opening, only when the real evaluated
            // loaded project set proves one unambiguous source owner.
            if (TryResolveRecoveryReadonlySourceProject(source) is { } project) source.SetProjectOwnership(project);
            return source;
        }
        catch { source.Dispose(); throw; }
    }
}
