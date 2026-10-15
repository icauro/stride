namespace Stride.Core.Settings;

public partial class SettingsContainer
{
    /// <summary>Validates native profile YAML without changing a live profile or any file.</summary>
    public void ValidateSettingsProfileSnapshot(ReadOnlyMemory<byte> bytes)
    {
        using var detached = ReadSettingsProfileSnapshot(bytes);
    }

    /// <summary>Restores exact native entries, including unknown keys and entry absence, without changing the file baseline.</summary>
    public void RestoreSettingsProfileSnapshot(SettingsProfile profile, ReadOnlyMemory<byte> bytes)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!ReferenceEquals(profile.Container, this) || ReferenceEquals(profile, RootProfile))
            throw new ArgumentException("Recovery requires a non-root profile owned by this container.", nameof(profile));
        using var detached = ReadSettingsProfileSnapshot(bytes);
        lock (SettingsLock)
        {
            var names = profile.Settings.Keys.Concat(detached.Settings.Keys).Distinct().ToArray();
            var before = names.Select(name => profile.CaptureAuthoredEntry(name)).ToArray();
            var after = names.Select(name => detached.CaptureAuthoredEntry(name)).ToArray();
            try
            {
                for (var index = 0; index < names.Length; index++) profile.RestoreAuthoredEntry(names[index], after[index]);
            }
            catch (Exception failure)
            {
                var errors = new List<Exception> { failure };
                for (var index = 0; index < names.Length; index++)
                    try { profile.RestoreAuthoredEntry(names[index], before[index]); }
                    catch (Exception rollback) { errors.Add(rollback); }
                if (errors.Count != 1) throw new AggregateException("Settings profile recovery and rollback failed.", errors);
                throw;
            }
        }
    }

    private SettingsProfile ReadSettingsProfileSnapshot(ReadOnlyMemory<byte> bytes)
    {
        var detached = CreateSettingsProfile(false, registerInContainer: false);
        try
        {
            using var stream = new MemoryStream(bytes.ToArray(), false);
            var result = SettingsYamlSerializer.Default.Deserialize(stream, new SettingsFile(detached));
            if (result is not SettingsFile file || !ReferenceEquals(file.Settings, detached))
                throw new InvalidDataException("The snapshot has no native settings profile.");
            return detached;
        }
        catch { detached.Dispose(); throw; }
    }
}
