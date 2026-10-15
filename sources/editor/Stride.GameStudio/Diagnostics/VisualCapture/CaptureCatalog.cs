using System.IO;
using System.Text.Json;

namespace Stride.GameStudio.Diagnostics.VisualCapture;

internal sealed class CaptureCatalog
{
    private sealed record Entry(string Id, string Kind, string Parent, string[] Types, string[] Names, string[] Automation, string[] Content, string[] Owners);
    private readonly Entry[] entries;
    internal CaptureCatalog(string path)
    {
        if (!File.Exists(path)) { entries = []; return; }
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        entries = document.RootElement.GetProperty("targets").EnumerateArray().Where(target => target.TryGetProperty("wpf", out _)).Select(target =>
        {
            var wpf = target.GetProperty("wpf"); var lookup = wpf.TryGetProperty("lookup", out var value) ? value : default;
            static string[] Values(JsonElement element, string key) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()).ToArray() : [];
            return new Entry(target.GetProperty("id").GetString(), target.GetProperty("kind").GetString(), target.TryGetProperty("parentId", out var parent) ? parent.GetString() : null,
                Values(lookup, "types"), Values(lookup, "names"), Values(lookup, "automationIds"), Values(lookup, "contentIds"), Values(lookup, "ownerTypes"));
        }).ToArray();
    }
    internal IReadOnlyList<CaptureTarget> Apply(IReadOnlyList<CaptureTarget> actual)
    {
        var byId = actual.ToDictionary(target => target.Id);
        IEnumerable<CaptureTarget> Ancestors(CaptureTarget target)
        {
            var visited = new HashSet<string>();
            for (var current = target; current != null && visited.Add(current.Id); current = current.ParentId != null ? byId.GetValueOrDefault(current.ParentId) : null) yield return current;
        }
        static bool MatchesType(CaptureTarget target, string type)
        {
            if (target.Type == type || target.Type?.Split('.').Last() == type) return true;
            var source = target.Source is CaptureDiscovery.DockSource dock ? dock.Visual ?? dock.Dock : target.Source;
            for (var current = source?.GetType(); current != null; current = current.BaseType) if (current.FullName == type || current.Name == type) return true;
            return false;
        }
        var changed = true;
        for (var pass = 0; pass < 64 && changed; pass++)
        {
            changed = false;
            foreach (var entry in entries)
            {
                // Source/binding/resource declarations without a precise identity remain unavailable.
                if (entry.Types.Length + entry.Names.Length + entry.Automation.Length + entry.Content.Length == 0) continue;
                foreach (var target in actual)
                {
                    if (target.SemanticIds.Contains(entry.Id)) continue;
                    if (entry.Types.Length != 0 && !entry.Types.Any(type => MatchesType(target, type))) continue;
                    if (entry.Names.Length != 0 && !entry.Names.Contains(target.FrameworkName) && !entry.Names.Contains(target.Name)) continue;
                    if (entry.Automation.Length != 0 && !entry.Automation.Contains(target.AutomationId)) continue;
                    if (entry.Content.Length != 0 && !entry.Content.Contains(target.ContentId)) continue;
                    var ancestors = Ancestors(target).ToArray();
                    if (entry.Owners.Length != 0 && !ancestors.Any(ancestor => entry.Owners.Any(type => MatchesType(ancestor, type)))) continue;
                    if (entry.Parent != null && !ancestors.Skip(1).Any(ancestor => ancestor.SemanticIds.Contains(entry.Parent))) continue;
                    target.SemanticIds = [.. target.SemanticIds, entry.Id]; changed = true;
                }
            }
        }
        var present = actual.SelectMany(target => target.SemanticIds).ToHashSet();
        return actual.Concat(entries.Where(entry => !present.Contains(entry.Id)).Select(entry => new CaptureTarget
        {
            Id = "known." + entry.Id, SemanticIds = [entry.Id], Kind = entry.Kind, Name = entry.Id, ParentId = entry.Parent,
            State = "unavailable", Reason = "No exact currently instantiated runtime target matches this source declaration."
        })).ToArray();
    }
}
