using System.Text.Json;
using System.Text.Json.Nodes;

namespace Stride.Editor.ImGui;

/// <summary>Reads startup choices without loading any graphics-dependent editor types.</summary>
internal static class EditorStartupPreferences
{
    internal static string LayoutPath(string[] args)
    {
        var privateRoot = EditorUserDataPaths.OverrideRoot;
        var index = Array.IndexOf(args, "--layout-file");
        if (index >= 0)
        {
            if (index + 1 >= args.Length) throw new ArgumentException("--layout-file requires a path.");
            return Path.GetFullPath(args[index + 1]);
        }
        return privateRoot != null ? Path.Combine(privateRoot, "Editor.ImGui", "layout.ini")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stride", "Editor.ImGui", "layout.ini");
    }

    private static string PathFor(string[] args) => Path.Combine(Path.GetDirectoryName(LayoutPath(args))!, "preferences.json");

    internal static string? GraphicsApi(string[] args)
    {
        if (args.Any(argument => argument is "--ui-test" or "--smoke-test" or "--measure-ui-controls" or "--graphics-capabilities")) return null;
        var path = PathFor(args);
        try
        {
            if (!File.Exists(path)) return null;
            if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new JsonException("Editor preferences exceed the supported size.");
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (!document.RootElement.TryGetProperty("Version", out var version) || version.GetInt32() != 1) return null;
            if (!document.RootElement.TryGetProperty("GraphicsApi", out var api)) return null;
            return api.GetString() is "Direct3D11" or "Direct3D12" or "Vulkan" ? api.GetString() : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        { Console.Error.WriteLine("Could not read the editor graphics preference: " + error.Message); return null; }
    }

    internal static void ResetGraphicsApi(string[] args)
    {
        var path = PathFor(args);
        var root = JsonNode.Parse(File.ReadAllBytes(path)) as JsonObject ?? throw new JsonException("Invalid editor preferences.");
        if (root["Version"]?.GetValue<int>() != 1) throw new JsonException("Unsupported editor preferences.");
        root["GraphicsApi"] = "Default";
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(root, new JsonSerializerOptions { WriteIndented = true });
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
