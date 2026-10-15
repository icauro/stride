namespace Stride.Editor.ImGui;

/// <summary>Real private preference files, atomic publication failure and explicit recovery; no UI input.</summary>
internal static class ShellPreferencesParityVerification
{
    internal static void Verify(string directory)
    {
        var path = Path.Combine(directory, "publication-failure.json");
        if (File.Exists(path)) throw new IOException("The private preferences verification already exists.");
        var store = new EditorPreferencesStore(path);
        Require(store.TryUpdate(value => value.RemoteHost = "before.example"), "The actual durable baseline must publish.");
        var baseline = store.Current; var bytes = File.ReadAllBytes(path);
        // Read sharing deliberately excludes delete/replace, exercising the
        // native atomic file publication rather than an invalid value shortcut.
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Require(!store.TryUpdate(value => value.RemoteHost = "after.example") && !string.IsNullOrEmpty(store.Error)
                && ReferenceEquals(store.Current, baseline) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes),
                "An actual locked-file publication must preserve the exact prior snapshot and durable bytes.");
            Require(Directory.GetFiles(directory, "publication-failure.json.*.tmp").Length == 0,
                "A rejected actual publication must remove its temporary file.");
        }
        Require(store.TryUpdate(value => value.RemoteHost = "after.example") && store.Error == null,
            "Removing the actual write obstruction must permit the normal retry.");
        var reopened = new EditorPreferencesStore(path); reopened.Load();
        Require(!reopened.IsBlocked && reopened.Current.RemoteHost == "after.example",
            "Independent load must read the successfully retried preferences.");
        foreach (var (name, text) in new[] { ("malformed", "{ malformed"), ("future", "{\"Version\":99}") })
        {
            var damagedPath = Path.Combine(directory, name + ".json");
            File.WriteAllText(damagedPath, text); var original = File.ReadAllBytes(damagedPath);
            var damaged = new EditorPreferencesStore(damagedPath); var previous = damaged.Current; damaged.Load();
            Require(damaged.IsBlocked && !string.IsNullOrEmpty(damaged.Error) && ReferenceEquals(damaged.Current, previous)
                && !damaged.Save() && !damaged.TryUpdate(value => value.RemoteHost = "must-not-publish")
                && File.ReadAllBytes(damagedPath).AsSpan().SequenceEqual(original),
                "Unreadable and future-version preferences must remain blocked and untouched until explicit recovery.");
            Require(damaged.RecoverDefaults() && !damaged.IsBlocked && damaged.Error == null,
                "Explicit recovery must publish valid defaults through the actual durable store.");
            var backups = Directory.GetFiles(directory, name + ".json.recovery.*.bak");
            Require(backups.Length == 1 && File.ReadAllBytes(backups[0]).AsSpan().SequenceEqual(original),
                "Explicit recovery must retain exact original unsupported bytes in its owned backup.");
            var recovered = new EditorPreferencesStore(damagedPath); recovered.Load();
            Require(!recovered.IsBlocked && recovered.Current.Version == 1
                && recovered.Current.RemoteHost == new EditorPreferences().RemoteHost && !damaged.RecoverDefaults(),
                "Independent recovery load must contain native defaults; an unblocked store must not recover again.");
        }
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
