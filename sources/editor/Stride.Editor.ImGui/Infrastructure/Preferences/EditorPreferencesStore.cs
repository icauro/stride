using System.Text.Json;

namespace Stride.Editor.ImGui;

/// <summary>Versioned local preferences; unsupported files remain untouched.</summary>
internal sealed class EditorPreferencesStore(string path)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private bool blocked;
    public EditorPreferences Current { get; private set; } = new();
    public string? Error { get; private set; }
    public bool IsBlocked => blocked;

    public void Load()
    {
        if (!File.Exists(path)) return;
        try
        {
            if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new JsonException("Editor preferences exceed the supported size.");
            var value = EditorPreferencesValidation.Validate(JsonSerializer.Deserialize<EditorPreferences>(File.ReadAllBytes(path), Options));
            EditorPreferencesValidation.RestoreComparers(value);
            Current = value;
            blocked = false; Error = null;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        { blocked = true; Error = error.Message; Console.Error.WriteLine("Editor preferences: " + Error); }
    }

    public bool RecoverDefaults()
    {
        if (!blocked) return false;
        try
        {
            if (File.Exists(path)) File.Copy(path, path + ".recovery." + Guid.NewGuid().ToString("N") + ".bak");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Error = error.Message; return false; }
        var previous = Current;
        Current = new(); blocked = false;
        if (Save()) return true;
        Current = previous; blocked = true; return false;
    }

    /// <summary>Publish a changed preference snapshot only after its durable write succeeds.</summary>
    public bool TryUpdate(Action<EditorPreferences> update)
    {
        if (blocked) return false;
        var next = JsonSerializer.Deserialize<EditorPreferences>(JsonSerializer.SerializeToUtf8Bytes(Current, Options), Options)!;
        EditorPreferencesValidation.RestoreComparers(EditorPreferencesValidation.Validate(next));
        update(next);
        if (!SaveValue(next)) return false;
        Current = next;
        return true;
    }

    public bool Save() => SaveValue(Current);

    private bool SaveValue(EditorPreferences value)
    {
        if (blocked) return false;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            EditorPreferencesValidation.Validate(value);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
            if (bytes.Length > 4 * 1024 * 1024) throw new JsonException("Editor preferences exceed the supported size.");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path, true); Error = null; return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { Error = error.Message; Console.Error.WriteLine("Editor preferences: " + Error); return false; }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Console.Error.WriteLine(error.Message); }
        }
    }
}
