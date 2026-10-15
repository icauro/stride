using System.Text;
using System.Text.Json;

namespace Stride.Editor.ImGui;

internal static class AssetFolderClipboardMetadata
{
    private const string Prefix = "# Stride-Asset-Folders: ";
    internal sealed record Payload(bool Code, string[] Paths, string[]? AssetPaths = null, string[]? CodePaths = null)
    {
        internal IEnumerable<string> ForMount(bool code) => AssetPaths != null || CodePaths != null
            ? (code ? CodePaths : AssetPaths) ?? [] : Code == code ? Paths : [];
        internal bool HasCode => ForMount(true).Any();
    }
    internal static string Attach(string yaml, bool code, IEnumerable<string> paths)
    {
        return Attach(yaml, code ? [] : paths, code ? paths : []);
    }
    internal static string Attach(string yaml, IEnumerable<string> assetPaths, IEnumerable<string> codePaths)
    {
        var assets = assetPaths.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var code = codePaths.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var payload = new Payload(assets.Length == 0, assets.Concat(code).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), assets, code);
        var text = Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload))) + "\n" + yaml;
        if (!TryRead(text, out _, out _)) throw new InvalidOperationException("The folder clipboard metadata exceeds its bounds.");
        return text;
    }
    internal static bool TryRead(string text, out string yaml, out Payload? payload)
    {
        yaml = text; payload = null;
        if (!text.StartsWith(Prefix, StringComparison.Ordinal)) return true;
        var newline = text.IndexOf('\n');
        if (newline < 0 || newline > 1024 * 1024) return false;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(Convert.FromBase64String(text[Prefix.Length..newline]), new JsonSerializerOptions { MaxDepth = 4 });
            if (payload?.Paths is not { Length: > 0 and <= 8192 } || payload.Paths.Concat(payload.AssetPaths ?? []).Concat(payload.CodePaths ?? []).Any(path => string.IsNullOrWhiteSpace(path) || path.Length > 4096
                || path.Contains('\\') || Path.IsPathRooted(path) || path.Split('/').Any(part => part is "" or "." or ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))) return false;
            if ((payload.AssetPaths?.Length ?? 0) + (payload.CodePaths?.Length ?? 0) > 8192) return false;
            yaml = text[(newline + 1)..]; return !string.IsNullOrWhiteSpace(yaml);
        }
        catch (Exception error) when (error is FormatException or JsonException or ArgumentException) { payload = null; return false; }
    }
}
