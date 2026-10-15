using System.Diagnostics;
using Stride.Core.IO;
using Stride.Core.MostRecentlyUsedFiles;
using Stride.Core.Settings;
using Stride.Core.Yaml;

namespace Stride.Editor.ImGui;

internal sealed record RecentProjectMetadata(string Path, string Version, long Timestamp);

/// <summary>Uses the native shared MRU key without loading any WPF settings assembly.</summary>
internal sealed class InternalSessionsSettings : IDisposable
{
    private readonly SettingsContainer container = new();
    private readonly SettingsKey<MRUDictionary> key;
    private readonly SettingsKey<string> lastNewSessionDirectory;
    private readonly SettingsKey<string> lastImportDirectory, lastOpenSessionDirectory;
    private readonly MostRecentlyUsedFileCollection mru;
    private readonly string path = Path.Combine(EditorUserDataPaths.SharedRoot, "GameStudioInternal.conf");
    private SettingsProfile? profile;
    private long nextRefresh;
    private (bool Exists, long Length, long Modified) observed;
    private RecentProjectMetadata[] entries = [];
    internal IReadOnlyList<RecentProjectMetadata> Entries => entries;
    internal string? Error { get; private set; }
    internal string? LastNewSessionDirectory { get; private set; }
    internal string? LastImportDirectory { get; private set; }
    internal string? LastOpenSessionDirectory { get; private set; }

    internal InternalSessionsSettings()
    {
        key = new("Internal/MostRecentlyUsedSessions", container, () => new MRUDictionary());
        lastNewSessionDirectory = new("Internal/TemplatesWindowDialogLastNewSessionTemplateDirectory", container, "");
        lastImportDirectory = new("Internal/FileDialogLastImportDirectory", container, "");
        lastOpenSessionDirectory = new("Internal/FileDialogLastOpenSessionDirectory", container, "");
        key.FallbackDeserializers.Add(ReadLegacy);
        mru = new(LoadLatest, key, Save, strictLoading: true);
    }

    internal void Refresh(bool force = false)
    {
        var now = Stopwatch.GetTimestamp();
        if (!force && now < nextRefresh) return;
        nextRefresh = now + Stopwatch.Frequency;
        try
        {
            var current = Stamp();
            if (!force && current == observed && profile != null && Error == null) return;
            mru.LoadFromSettings();
            var nextDirectory = profile!.ContainsKey(lastNewSessionDirectory) ? lastNewSessionDirectory.GetValue(profile, false) : null;
            var nextImportDirectory = profile.ContainsKey(lastImportDirectory) ? lastImportDirectory.GetValue(profile, false) : null;
            var nextOpenDirectory = profile.ContainsKey(lastOpenSessionDirectory) ? lastOpenSessionDirectory.GetValue(profile, false) : null;
            var nextEntries = mru.MostRecentlyUsedFiles.Select(item =>
            {
                if (item.FilePath == null || item.Version == null) throw new IOException("The native recent-project entry is invalid.");
                return new RecentProjectMetadata(item.FilePath.ToOSPath(), item.Version, item.Timestamp);
            }).ToArray();
            entries = nextEntries; LastNewSessionDirectory = nextDirectory;
            LastImportDirectory = nextImportDirectory; LastOpenSessionDirectory = nextOpenDirectory;
            observed = current; Error = null;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException)
        { Error = failure.Message; }
    }

    internal void Add(string file, string version) => Mutate(() => mru.AddFile(new UFile(file), version));
    internal void RememberNewSessionDirectory(string directory)
        => Mutate(() => { lastNewSessionDirectory.SetValue(directory, profile!); Save(); });
    internal void RememberImportSelection(string file)
        => Mutate(() => { lastImportDirectory.SetValue(file, profile!); Save(); });
    internal void RememberOpenedSessionDirectory(string directory)
        => Mutate(() => { lastOpenSessionDirectory.SetValue(directory, profile!); Save(); });
    internal void Remove(string file, IReadOnlyCollection<string> versions)
        => Mutate(() => { foreach (var version in versions) mru.RemoveFile(new UFile(file), version); });
    internal void Clear(IReadOnlyCollection<string> versions)
        => Mutate(() => { foreach (var version in versions) mru.Clear(version); });
    private void Mutate(Action action)
    {
        try
        {
            LoadLatest(); action(); Refresh(true);
            if (Error != null) throw new IOException(Error);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException)
        { Error = failure.Message; throw; }
    }

    private SettingsProfile LoadLatest()
    {
        var exists = File.Exists(path);
        var next = exists ? container.LoadSettingsProfile(new UFile(path), false)
            ?? throw new IOException("The native recent-project settings could not be read.") : container.CreateSettingsProfile(false);
        var previous = profile;
        container.CurrentProfile = next; profile = next;
        if (previous != null) { container.UnloadSettingsProfile(previous); previous.Dispose(); }
        return next;
    }
    private void Save()
    {
        var current = profile ?? throw new InvalidOperationException("The native MRU profile is unavailable.");
        var bytes = container.SerializeSettingsProfile(current);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var saving = container.BeginGuardedProfileSave(current);
        var expected = current.LoadedContentHash.IsEmpty ? null : current.LoadedContentHash.ToArray();
        var receipt = WindowsOwnedAssetFileWriter.Write(path, expected, bytes);
        container.AcknowledgeSavedSettingsProfile(current, new UFile(path), receipt.ContentHash);
    }
    private (bool, long, long) Stamp()
    {
        var info = new FileInfo(path);
        return info.Exists ? (true, info.Length, info.LastWriteTimeUtc.Ticks) : (false, 0, 0);
    }
    private static object ReadLegacy(EventReader reader)
    {
        var files = (List<UFile>)SettingsYamlSerializer.Default.Deserialize(reader, typeof(List<UFile>));
        var time = DateTime.UtcNow.Ticks;
        // Exact native legacy dictionary identity; never infer a version for JSON paths.
        return new MRUDictionary { ["1.3"] = files.Select(file => new MostRecentlyUsedFile(file) { Timestamp = time-- }).ToList() };
    }
    public void Dispose()
    {
        if (profile == null) return;
        var current = profile; container.CurrentProfile = container.RootProfile;
        container.UnloadSettingsProfile(current); current.Dispose(); profile = null;
    }
}
