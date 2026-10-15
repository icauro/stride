using System.Diagnostics;
using Stride.GameStudio.Helpers;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private string[] presentedRecentProjects = [];
    private InternalSessionsSettings? nativeRecentProjects;
    private RecentProjectMetadata[] presentedRecentMetadata = [];
    private IReadOnlyList<RecentProjectMetadata>? observedNativeRecentProjects;
    private string[] observedCompatibleRecentProjects = [];
    private IReadOnlyList<RecentProjectMetadata> PresentedRecentProjectMetadata()
    {
        nativeRecentProjects?.Refresh();
        var native = nativeRecentProjects?.Entries;
        var compatible = preferences?.Current.RecentProjects;
        var unchanged = ReferenceEquals(native, observedNativeRecentProjects) && observedCompatibleRecentProjects.Length == (compatible?.Count ?? 0);
        for (var index = 0; unchanged && index < observedCompatibleRecentProjects.Length; index++)
            unchanged = string.Equals(observedCompatibleRecentProjects[index], compatible![index], StringComparison.Ordinal);
        if (unchanged) return presentedRecentMetadata;
        observedNativeRecentProjects = native;
        observedCompatibleRecentProjects = compatible?.ToArray() ?? [];
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var presented = new List<RecentProjectMetadata>();
        if (native != null)
            foreach (var item in native)
            {
                // A known newer-version path cannot re-enter through legacy JSON.
                known.Add(item.Path);
                if (string.Compare(item.Version, StrideGameStudio.EditorVersionMajor, StringComparison.Ordinal) <= 0) presented.Add(item);
            }
        foreach (var path in observedCompatibleRecentProjects)
            if (known.Add(path)) presented.Add(new(path, "", 0)); // Unknown version remains unknown.
        presentedRecentMetadata = presented.Take(10).ToArray();
        presentedRecentProjects = presentedRecentMetadata.Select(item => item.Path).ToArray();
        return presentedRecentMetadata;
    }
    private IReadOnlyList<string> PresentedRecentProjects()
    {
        PresentedRecentProjectMetadata();
        return presentedRecentProjects;
    }

    private string? ResolveRecentProjectPath(string path)
    {
        nativeRecentProjects?.Refresh(true);
        if (nativeRecentProjects?.Error is { } error) throw new IOException(error);
        var fullPath = Path.GetFullPath(path);
        var native = nativeRecentProjects?.Entries.FirstOrDefault(candidate => string.Equals(candidate.Path, fullPath, StringComparison.OrdinalIgnoreCase));
        if (native != null) return string.Compare(native.Version, StrideGameStudio.EditorVersionMajor, StringComparison.Ordinal) <= 0 ? native.Path : null;
        return preferences?.Current.RecentProjects.FirstOrDefault(candidate => string.Equals(candidate, fullPath, StringComparison.OrdinalIgnoreCase));
    }

    private string RequireRecentProjectPath(string path)
        => ResolveRecentProjectPath(path) ?? throw new InvalidOperationException("The recent project is no longer in the list.");

    private void RequestRecentProjectOpen(string path)
    {
        var actual = RequireRecentProjectPath(path);
        RequestReplace(() => StartOpenProject(RequireRecentProjectPath(actual)), true, true);
    }

    private void ExploreRecentProject(string path)
    {
        var actual = RequireRecentProjectPath(path);
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        start.ArgumentList.Add("/select,");
        start.ArgumentList.Add(actual);
        Process.Start(start)?.Dispose();
    }

    private void CopyRecentProjectPath(string path, bool folder)
    {
        var actual = RequireRecentProjectPath(path);
        var text = folder ? Path.GetDirectoryName(actual) ?? "" : actual;
        try { WindowsScriptClipboard.TryWrite(GetCurrentDialogOwner(), text, false); }
        catch (SystemException) { } // Original SafeClipboard action provides no failure feedback.
    }

    private void RemoveRecentProject(string path)
    {
        var actual = RequireRecentProjectPath(path);
        var versions = PresentedRecentProjectMetadata().Where(item => item.Version.Length != 0).Select(item => item.Version).Distinct(StringComparer.Ordinal).ToArray();
        nativeRecentProjects?.Remove(actual, versions);
        preferences!.Current.RecentProjects.RemoveAll(candidate => string.Equals(candidate, actual, StringComparison.OrdinalIgnoreCase));
        SaveEditorPreferences();
    }
}
