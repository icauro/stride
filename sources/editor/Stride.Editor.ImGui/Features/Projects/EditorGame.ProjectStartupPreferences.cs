namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private ProjectStartupSettings? nativeProjectStartup;
    private void RefreshProjectStartupPreferences(bool force = false)
    {
        nativeProjectStartup?.Refresh(force);
        if (preferences == null) return;
        if (preferences.Current.DefaultSessionEditorVersion is { } knownVersion
            && knownVersion != Stride.GameStudio.Helpers.StrideGameStudio.EditorVersion)
            preferences.Current.DefaultSessionPath = "";
        if (nativeProjectStartup?.Error != null) return;
        if (nativeProjectStartup?.ReloadLastSession is { } reload) preferences.Current.ReloadLastSession = reload;
        if (nativeProjectStartup?.StartupSession is { } startup)
        {
            preferences.Current.DefaultSessionPath = startup;
            preferences.Current.DefaultSessionEditorVersion = Stride.GameStudio.Helpers.StrideGameStudio.EditorVersion;
        }
    }
    private bool ReadReloadLastSession()
    {
        RefreshProjectStartupPreferences();
        return preferences?.Current.ReloadLastSession == true;
    }
    private void WriteReloadLastSession(bool value)
    {
        if (preferences == null) return;
        nativeProjectStartup?.WriteReload(value);
        preferences.Current.ReloadLastSession = value;
        SaveEditorPreferences();
    }
    private bool PublishProjectStartupSettings(string defaultSession, bool reload)
    {
        try { nativeProjectStartup?.Write(reload, defaultSession); return true; }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException)
        { editorSettingsError = failure.Message; return false; }
    }
}
