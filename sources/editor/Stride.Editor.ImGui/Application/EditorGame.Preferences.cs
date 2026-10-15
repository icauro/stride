namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private EditorPreferencesStore? preferences;
    private bool UseUserPreferences => !smokeTest && !args.Contains("--ui-test") && !args.Contains("--measure-ui-controls") && !args.Contains("--native-input-test") && !args.Contains("--native-close-test");
    private void LoadEditorPreferences()
    {
        if (!UseUserPreferences) { EditorLocalization.Initialize(Stride.Core.Assets.Editor.SupportedLanguage.English); return; }
        preferences = new EditorPreferencesStore(Path.Combine(Path.GetDirectoryName(settingsPath)!, "preferences.json"));
        preferences.Load();
        nativeRecentProjects = new InternalSessionsSettings();
        nativeRecentProjects.Refresh(true);
        nativeProjectStartup = new ProjectStartupSettings();
        RefreshProjectStartupPreferences(true);
        EditorLocalization.Initialize(preferences.Current.Language);
        RestorePanelVisibility(preferences.Current);
        assetTileView = preferences.Current.AssetTileView;
        assetTileThumbnailSize = preferences.Current.AssetTileThumbnailSize;
        projectPathInput = preferences.Current.LastProjectPath ?? "";
    }
    private void SaveEditorPreferences()
    {
        if (preferences == null) return;
        CapturePanelVisibility(preferences.Current);
        preferences.Save();
    }
    private void RestoreLastEditorSession()
    {
        if (projectLoading != null) return;
        RefreshProjectStartupPreferences(true);
        if (preferences?.Current.DefaultSessionPath is { Length: > 0 } configuredSession)
        {
            StartOpenProject(configuredSession);
            return;
        }
        if (preferences?.Current.ReloadLastSession != true) return;
        nativeRecentProjects?.Refresh(true);
        // Program selects the timestamp-first native MRU, without the menu's
        // current-or-older filter or a file-existence preflight.
        var recent = nativeRecentProjects?.Error == null ? nativeRecentProjects?.Entries.FirstOrDefault()?.Path : null;
        var path = recent ?? preferences.Current.RecentProjects.FirstOrDefault() ?? preferences.Current.LastProjectPath;
        if (!string.IsNullOrWhiteSpace(path)) StartOpenProject(path);
    }
    private void RememberProjectPath(string path)
    {
        if (preferences == null) return;
        preferences.Current.LastProjectPath = Path.GetFullPath(path);
        preferences.Current.DialogDirectories["project"] = Path.GetDirectoryName(Path.GetFullPath(path))!;
        SaveEditorPreferences();
    }
    private void RememberOpenedProject(string path)
    {
        if (preferences == null) return;
        var fullPath = Path.GetFullPath(path);
        try
        {
            nativeRecentProjects?.Add(fullPath, Stride.GameStudio.Helpers.StrideGameStudio.EditorVersionMajor);
            nativeRecentProjects?.RememberOpenedSessionDirectory(Path.GetDirectoryName(fullPath)!);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // MRU persistence is ancillary to a successfully adopted real project.
            // Keep the compatibility path while reporting the genuine native failure.
            Console.Error.WriteLine("Recent projects: " + failure.Message);
        }
        preferences.Current.RecentProjects.RemoveAll(item => string.Equals(item, fullPath, StringComparison.OrdinalIgnoreCase));
        preferences.Current.RecentProjects.Insert(0, fullPath);
        if (preferences.Current.RecentProjects.Count > 20) preferences.Current.RecentProjects.RemoveRange(20, preferences.Current.RecentProjects.Count - 20);
        RememberProjectPath(fullPath);
    }
    private void ClearRecentProjects()
    {
        var versions = PresentedRecentProjectMetadata().Where(item => item.Version.Length != 0).Select(item => item.Version).Distinct(StringComparer.Ordinal).ToArray();
        nativeRecentProjects?.Clear(versions);
        preferences?.Current.RecentProjects.Clear(); SaveEditorPreferences();
    }
}
