using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Stride.Editor.ImGui;

/// <summary>One layout publication contains both native placement and certified presentation membership.</summary>
internal static class EditorDocumentGroupLayoutCodec
{
    private const string Header = "[Stride][DocumentGroups]";
    private const int MaximumGroups = 256, MaximumDocuments = 16384, MaximumPayload = 2 * 1024 * 1024;
    private static readonly Regex Section = new(@"(?m)^\[Stride\]\[DocumentGroups\]\r?\n(?:[^\[\r\n][^\r\n]*\r?\n?|\r?\n)*", RegexOptions.CultureInvariant);

    internal static string Extract(string layout, out EditorDocumentGroupsLayout? membership)
    {
        membership = null;
        var sections = Section.Matches(layout);
        if (sections.Count == 0) return layout;
        var native = Section.Replace(layout, "");
        if (sections.Count != 1) throw new InvalidDataException("Duplicate document-group layout sections.");
        var lines = sections[0].Value.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToArray();
        if (lines.Length != 3 || lines[0] != Header || !lines[1].StartsWith("NativeSha256=", StringComparison.Ordinal)
            || !lines[2].StartsWith("Data=", StringComparison.Ordinal))
            throw new InvalidDataException("Invalid document-group layout section.");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(native)));
        if (!string.Equals(hash, lines[1][13..], StringComparison.Ordinal))
            throw new InvalidDataException("Document membership does not match the native layout.");
        if (lines[2].Length > MaximumPayload * 4 / 3 + 8) throw new InvalidDataException("Document-group layout is too large.");
        var payload = Convert.FromBase64String(lines[2][5..]);
        if (payload.Length > MaximumPayload) throw new InvalidDataException("Document-group layout is too large.");
        membership = JsonSerializer.Deserialize<EditorDocumentGroupsLayout>(payload, new JsonSerializerOptions { MaxDepth = 16 })
            ?? throw new InvalidDataException("Document-group layout is empty.");
        Validate(membership);
        return native;
    }

    internal static string Append(string native, EditorDocumentGroupsLayout? membership)
    {
        native = Section.Replace(native, "");
        if (membership == null) return native;
        Validate(membership);
        var payload = JsonSerializer.SerializeToUtf8Bytes(membership);
        if (payload.Length > MaximumPayload) throw new InvalidDataException("Document-group layout is too large.");
        native = native.TrimEnd('\r', '\n') + "\n";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(native)));
        return native + Header + "\nNativeSha256=" + hash + "\nData=" + Convert.ToBase64String(payload) + "\n";
    }

    private static void Validate(EditorDocumentGroupsLayout layout)
    {
        if (layout.Version != 1 || string.IsNullOrWhiteSpace(layout.Workspace) || layout.Workspace.Length > 32768
            || layout.Groups == null || layout.Groups.Length is < 1 or > MaximumGroups
            || !layout.Groups.Any(group => group?.Id == EditorDocumentGroups.PrimaryId))
            throw new InvalidDataException("Invalid document-group layout identity or version.");
        var ids = new HashSet<Guid>(); var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        foreach (var group in layout.Groups)
        {
            if (group == null || group.Id == Guid.Empty || !ids.Add(group.Id) || group.Paths == null
                || (count += group.Paths.Length) > MaximumDocuments)
                throw new InvalidDataException("Invalid document-group membership.");
            foreach (var path in group.Paths)
                if (string.IsNullOrWhiteSpace(path) || path.Length > 32768 || path.IndexOf('\0') >= 0 || !paths.Add(path))
                    throw new InvalidDataException("Invalid or duplicate document-group path.");
            if (group.ActivePath != null && !group.Paths.Contains(group.ActivePath, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("The selected document is not a member of its group.");
        }
        if (layout.ActivePath != null && !paths.Contains(layout.ActivePath))
            throw new InvalidDataException("The active document is not a member of the saved layout.");
    }
}
