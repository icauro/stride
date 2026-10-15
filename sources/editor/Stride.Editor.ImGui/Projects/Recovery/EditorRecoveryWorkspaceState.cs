using System.Text;

namespace Stride.Editor.ImGui;

/// <summary>Detached presentation identities only. Restoration must resolve each descriptor through the current native document owners.</summary>
internal sealed record EditorRecoveryOpenDocument(string Path, string Kind, string? AssetId,
    string? PackagePath, string? Namespace, string? SourceProjectPath = null,
    string? GeneratedByAssetId = null, bool ReadOnlyPresentation = false);

internal sealed record EditorRecoveryWorkspaceState(int Version, string ProjectPath, string NativeIni,
    EditorDocumentGroupsLayout Groups, EditorRecoveryOpenDocument[] Documents, string? ActivePath)
{
    internal EditorRecoveryWorkspaceState Snapshot(string projectPath)
    {
        if (Version != 1 || !SamePath(ProjectPath, projectPath) || NativeIni == null
            || Encoding.UTF8.GetByteCount(NativeIni) > 4 * 1024 * 1024 || NativeIni.Contains('\0')
            || !NativeIni.Contains("[Window][", StringComparison.Ordinal)
            || Groups is not { Version: 1 } || string.IsNullOrWhiteSpace(Groups.Workspace)
            || Groups.Groups == null || Groups.Groups.Length == 0 || Groups.Groups.Length > 256
            || !Groups.Groups.Any(group => group != null && group.Id == EditorDocumentGroups.PrimaryId)
            || Documents == null || Documents.Length > 4096)
            throw new InvalidDataException("Recovery workspace presentation metadata is invalid.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var documents = Documents.Select(document =>
        {
            if (document == null || !CanonicalPath(document.Path) || string.IsNullOrWhiteSpace(document.Kind)
                || document.Kind.Length > 128 || !paths.Add(document.Path)
                || (document.PackagePath != null && !CanonicalPath(document.PackagePath))
                || (document.SourceProjectPath != null && !CanonicalPath(document.SourceProjectPath)))
                throw new InvalidDataException("Recovery workspace document identity is invalid.");
            if (document.GeneratedByAssetId != null && (!Guid.TryParse(document.GeneratedByAssetId, out var producer) || producer == Guid.Empty
                || document.Kind != typeof(ScriptSourceDocument).FullName || document.AssetId != null
                || document.PackagePath == null || document.SourceProjectPath == null || !document.ReadOnlyPresentation))
                throw new InvalidDataException("Recovery generated-source presentation identity is invalid.");
            if (document.ReadOnlyPresentation && document.Kind != typeof(ScriptSourceDocument).FullName
                && document.Kind != typeof(AssetScriptSourceDocument).FullName)
                throw new InvalidDataException("Recovery read-only source presentation has another document kind.");
            return document with { };
        }).ToArray();
        var membership = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groupIds = new HashSet<Guid>();
        var groups = Groups.Groups.Select(group =>
        {
            if (group == null || group.Id == Guid.Empty || !groupIds.Add(group.Id) || group.Paths == null
                || group.Paths.Length > documents.Length || group.Paths.Any(path => path == null || !paths.Contains(path) || !membership.Add(path))
                || (group.ActivePath != null && !group.Paths.Contains(group.ActivePath, StringComparer.OrdinalIgnoreCase)))
                throw new InvalidDataException("Recovery workspace group membership is invalid.");
            return group with { Paths = group.Paths.ToArray() };
        }).ToArray();
        if (membership.Count != paths.Count || (ActivePath != null && !paths.Contains(ActivePath)))
            throw new InvalidDataException("Recovery workspace active document is invalid.");
        if (Groups.ActivePath != null && (!membership.Contains(Groups.ActivePath)
            || !string.Equals(Groups.ActivePath, ActivePath, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Recovery workspace and document-group active owners disagree.");
        return this with { Documents = documents, Groups = Groups with { Groups = groups, ActivePath = Groups.ActivePath ?? ActivePath } };
    }

    private static bool CanonicalPath(string path) => !string.IsNullOrWhiteSpace(path)
        && path.Length <= 32768 && Path.IsPathFullyQualified(path)
        && string.Equals(Path.GetFullPath(path), path, StringComparison.OrdinalIgnoreCase);
    private static bool SamePath(string first, string second) => CanonicalPath(first)
        && string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
}
