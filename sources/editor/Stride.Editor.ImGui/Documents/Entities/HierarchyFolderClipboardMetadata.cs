using System.Text;
using System.Text.Json;

namespace Stride.Editor.ImGui;

// Only folder objects absent from EntityDesign need this extra metadata. The
// authored hierarchy remains the original Stride CopyPasteService YAML payload.
internal static class HierarchyFolderClipboardMetadata
{
    private const string Prefix = "# StrideHierarchyFoldersV1:";
    internal static string Attach(string authoredYaml, IEnumerable<(EntityHierarchyDocument Document, Guid? Owner, string Path)> selected)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folders = selected.ToArray();
        foreach (var folder in folders)
        {
            if (folders.Any(other => ReferenceEquals(other.Document, folder.Document) && other.Owner == folder.Owner
                && !string.Equals(other.Path, folder.Path, StringComparison.OrdinalIgnoreCase)
                && folder.Path.StartsWith(other.Path + "/", StringComparison.OrdinalIgnoreCase))) continue;
            var rootIds = folder.Document.GetSelectedTransformRoots().Select(entity => entity.Id).ToHashSet();
            var ancestor = folder.Owner is { } owner && folder.Document.Asset.Hierarchy.Parts.TryGetValue(owner, out var parent) ? parent.Entity.Transform : null;
            var beneathEntity = false;
            for (; ancestor != null; ancestor = ancestor.Parent)
                if (rootIds.Contains(ancestor.Entity.Id)) { beneathEntity = true; break; }
            if (beneathEntity) continue;
            var prefix = folder.Path.LastIndexOf('/') + 1;
            foreach (var path in folder.Document.GetHierarchyFolders(folder.Owner))
                if (string.Equals(path, folder.Path, StringComparison.OrdinalIgnoreCase) || path.StartsWith(folder.Path + "/", StringComparison.OrdinalIgnoreCase))
                    paths.Add(path[prefix..]);
        }
        if (paths.Count == 0) return authoredYaml;
        var metadata = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(paths.Order(StringComparer.OrdinalIgnoreCase).ToArray())));
        var text = Prefix + metadata + "\n" + authoredYaml;
        if (!TryRead(text, out _, out _)) throw new InvalidOperationException("The selected folder metadata exceeds the clipboard bounds.");
        return text;
    }
    internal static bool TryRead(string text, out string authoredYaml, out string[] paths)
    {
        authoredYaml = text; paths = [];
        if (!text.StartsWith(Prefix, StringComparison.Ordinal)) return true;
        var newline = text.IndexOf('\n');
        if (newline < 0 || newline > 1024 * 1024) return false;
        try
        {
            var bytes = Convert.FromBase64String(text[Prefix.Length..newline].TrimEnd('\r'));
            paths = JsonSerializer.Deserialize<string[]>(bytes, new JsonSerializerOptions { MaxDepth = 4 }) ?? [];
            if (paths.Length is 0 or > 8192 || paths.Any(path => !ValidPath(path))) { paths = []; return false; }
            paths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            authoredYaml = text[(newline + 1)..];
            return !string.IsNullOrWhiteSpace(authoredYaml);
        }
        catch (Exception error) when (error is FormatException or JsonException or ArgumentException) { paths = []; return false; }
    }
    private static bool ValidPath(string? path) => !string.IsNullOrWhiteSpace(path) && path.Length <= 4096
        && !path.Any(char.IsControl) && !path.Contains('\\')
        && path.Split('/').All(part => part.Length > 0 && part is not ("." or ".."));
}
