using System.Runtime.CompilerServices;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class SpriteCommonNumericRow
    {
        internal PropertyNumericRange?[] Ranges = [];
        internal PropertyNumericRange? Merged;
    }
    private readonly ConditionalWeakTable<SpriteCommonSetting, Dictionary<int, SpriteCommonNumericRow>> spriteCommonNumericRows = new();
    private SpriteCommonNumericRow GetSpriteCommonNumericRow(AssetMetadataDocument document, SpriteCommonSetting setting, int axis)
    {
        var rows = spriteCommonNumericRows.GetValue(setting, static _ => new());
        if (!rows.TryGetValue(axis, out var row)) rows[axis] = row = new();
        PropertyNumericRange? Range(int index)
        {
            var field = setting.Fields[index];
            return field.Type == typeof(Stride.Core.Mathematics.Vector2) || field.Type == typeof(Stride.Core.Mathematics.Vector4)
                || field.Type == typeof(Stride.Core.Mathematics.Rectangle)
                ? document.SpriteAxisNumericRanges(field).ElementAtOrDefault(axis) : document.SpriteNumericRange(field);
        }
        var changed = row.Ranges.Length != setting.Fields.Length;
        if (!changed) for (var index = 0; index < row.Ranges.Length; index++)
            if (!ReferenceEquals(row.Ranges[index], Range(index))) { changed = true; break; }
        if (changed)
        {
            row.Ranges = Enumerable.Range(0, setting.Fields.Length).Select(Range).ToArray();
            row.Merged = NativeNumericInput.Merge(SpriteCoordinateType(setting.First.Type), row.Ranges);
        }
        return row;
    }
}
