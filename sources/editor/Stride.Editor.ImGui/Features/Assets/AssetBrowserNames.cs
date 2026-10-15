using System.Runtime.CompilerServices;

namespace Stride.Editor.ImGui;

/// <summary>Visible native asset names; the entry's original URL remains unchanged.</summary>
internal static class AssetBrowserNames
{
    private sealed record Names(string Display, string Natural);
    private static readonly ConditionalWeakTable<AssetEntry, Names> values = new();
    private static Names Get(AssetEntry asset) => values.GetValue(asset, static entry =>
    {
        var leaf = Path.GetFileName(entry.Name.Replace('/', Path.DirectorySeparatorChar));
        return new(leaf, leaf.Replace(" ", ""));
    });
    internal static string Display(AssetEntry asset) => Get(asset).Display;
    internal static string Natural(AssetEntry asset) => Get(asset).Natural;
}
