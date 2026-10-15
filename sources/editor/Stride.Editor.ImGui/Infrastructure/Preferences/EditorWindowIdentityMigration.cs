using System.Text.RegularExpressions;
using Hexa.NET.ImGui;

namespace Stride.Editor.ImGui;

/// <summary>Adapts known legacy native window IDs in loaded INI bytes; never writes the user's file.</summary>
internal static class EditorWindowIdentityMigration
{
    private static readonly Regex Headers = new(@"(?m)^\[Window\]\[(?<name>[^\]\r\n]+)\](?=\r?$)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex DockReferences = new(@"\b(?<key>Selected|Window|ViewportId)=0x(?<id>[0-9A-Fa-f]{8})\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    internal static string Migrate(string layout)
    {
        if (layout.Length > 4 * 1024 * 1024 || layout.IndexOf('\0') >= 0)
            throw new InvalidDataException("Editor layout exceeds the supported migration bounds.");
        var names = EditorPanels.All.Where(panel => panel.CaptureId is not ("editor.document" or "sprites.editor")
                && !panel.WindowName.Contains("###", StringComparison.Ordinal))
            .Select(panel => (Old: panel.WindowName, Current: panel.DisplayWindowName))
            .Concat(new[] { (Old: "Project loading", Current: EditorLocalization.Widget("Project loading")) }).ToArray();
        var existing = Headers.Matches(layout).Select(match => match.Groups["name"].Value).ToArray();
        var captions = new Dictionary<string, string>(StringComparer.Ordinal);
        var identifiers = new Dictionary<uint, uint>();
        foreach (var name in names)
        {
            if (!existing.Contains(name.Old, StringComparer.Ordinal)) continue;
            var target = ImGuiP.ImHashStr(name.Current, (nuint)0, 0);
            // An already saved canonical window is authoritative. Do not merge
            // two independently positioned settings blocks or replace its size.
            if (existing.Any(caption => caption.Contains("###", StringComparison.Ordinal)
                && ImGuiP.ImHashStr(caption, (nuint)0, 0) == target)) continue;
            captions.Add(name.Old, name.Current);
            identifiers.Add(ImGuiP.ImHashStr(name.Old, (nuint)0, 0), target);
        }
        if (captions.Count == 0) return layout;
        layout = Headers.Replace(layout, match => captions.TryGetValue(match.Groups["name"].Value, out var caption)
            ? "[Window][" + caption + "]" : match.Value);
        var docking = false;
        return Regex.Replace(layout, @"(?m)^.*$", match =>
        {
            var line = match.Value;
            if (line.StartsWith("[", StringComparison.Ordinal)) docking = line.TrimEnd('\r') == "[Docking][Data]";
            if (!docking && !line.StartsWith("ViewportId=", StringComparison.Ordinal)) return line;
            return DockReferences.Replace(line, reference =>
            {
                var previous = Convert.ToUInt32(reference.Groups["id"].Value, 16);
                return identifiers.TryGetValue(previous, out var current)
                    ? reference.Groups["key"].Value + "=0x" + current.ToString("X8") : reference.Value;
            });
        }, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }
}
