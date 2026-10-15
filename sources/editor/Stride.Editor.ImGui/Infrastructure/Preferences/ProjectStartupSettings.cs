using System.Diagnostics;
using Stride.Core.IO;
using Stride.Core.Settings;
using Stride.GameStudio.Helpers;

namespace Stride.Editor.ImGui;

/// <summary>The two native project-startup keys; unrelated settings stay raw.</summary>
internal sealed class ProjectStartupSettings : IDisposable
{
    private readonly SettingsContainer container = new();
    private readonly SettingsKey<bool> reload;
    private readonly SettingsKey<UFile> startup;
    private readonly string path = Path.Combine(EditorUserDataPaths.SharedRoot, "GameStudioSettings.conf");
    private SettingsProfile? profile;
    private long nextRefresh;
    private (bool Exists, long Length, long Modified) observed;
    internal bool? ReloadLastSession { get; private set; }
    internal string? StartupSession { get; private set; }
    internal string? Error { get; private set; }
    internal ProjectStartupSettings()
    {
        reload = new("Interface/ReloadLastSession", container, false);
        startup = new("Interface/StartupSession" + StrideGameStudio.EditorVersion, container, "");
    }
    internal void Refresh(bool force = false)
    {
        var now = Stopwatch.GetTimestamp();
        if (!force && now < nextRefresh) return;
        nextRefresh = now + Stopwatch.Frequency;
        try
        {
            var stamp = Stamp();
            if (!force && profile != null && stamp == observed && Error == null) return;
            PublishSnapshot(LoadLatest()); observed = stamp; Error = null;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException)
        { Error = failure.Message; }
    }
    internal void WriteReload(bool value) => Write(value, null);
    internal void Write(bool reloadLast, string? startupPath)
    {
        try
        {
            var latest = LoadLatest();
            reload.SetValue(reloadLast, latest);
            if (startupPath != null) startup.SetValue(new UFile(startupPath), latest);
            var bytes = container.SerializeSettingsProfile(latest);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var saving = container.BeginGuardedProfileSave(latest);
            var hash = latest.LoadedContentHash.IsEmpty ? null : latest.LoadedContentHash.ToArray();
            var receipt = WindowsOwnedAssetFileWriter.Write(path, hash, bytes);
            container.AcknowledgeSavedSettingsProfile(latest, new UFile(path), receipt.ContentHash);
            PublishSnapshot(latest); observed = Stamp(); Error = null;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException)
        { Error = failure.Message; throw; }
    }
    private void PublishSnapshot(SettingsProfile value)
    {
        var nextReload = value.ContainsKey(reload) ? reload.GetValue(value, false) : (bool?)null;
        var nextStartup = value.ContainsKey(startup) ? startup.GetValue(value, false).ToOSPath() : null;
        ReloadLastSession = nextReload; StartupSession = nextStartup;
    }
    private SettingsProfile LoadLatest()
    {
        var next = File.Exists(path) ? container.LoadSettingsProfile(new UFile(path), false)
            ?? throw new IOException("The native project-startup settings could not be read.") : container.CreateSettingsProfile(false);
        var previous = profile; container.CurrentProfile = next; profile = next;
        if (previous != null) { container.UnloadSettingsProfile(previous); previous.Dispose(); }
        return next;
    }
    private (bool, long, long) Stamp()
    {
        var info = new FileInfo(path);
        return info.Exists ? (true, info.Length, info.LastWriteTimeUtc.Ticks) : (false, 0, 0);
    }
    public void Dispose()
    {
        if (profile == null) return;
        var current = profile; container.CurrentProfile = container.RootProfile;
        container.UnloadSettingsProfile(current); current.Dispose(); profile = null;
    }
}
