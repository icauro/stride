using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal static class NativeAbstractEntryControl
{
    internal static bool IsMixed(IReadOnlyList<AbstractNodeEntry> entries, IReadOnlyList<object?> values)
    {
        if (values.Count < 2) return false;
        var selectable = entries.SelectMany(entry => entry is AbstractNodeTypeGroup group ? group.Types.Cast<AbstractNodeEntry>() : new[] { entry });
        var selected = selectable.FirstOrDefault(entry => entry.IsMatchingValue(values[0]!));
        return selected != null ? values.Skip(1).Any(value => !selected.IsMatchingValue(value!))
            : values.Skip(1).Any(value => value?.GetType() != values[0]?.GetType());
    }
    internal static void Choices(IReadOnlyList<AbstractNodeEntry> entries, object? current, Action<AbstractNodeEntry> select,
        Func<AbstractNodeEntry, bool>? canSelect = null)
    {
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            Gui.PushID(index);
            try
            {
            if (entry is AbstractNodeTypeGroup group)
            {
                if (!Gui.BeginMenu(group.DisplayValue)) continue;
                try
                {
                    var childIndex = 0;
                    foreach (var type in group.Types)
                    {
                        Gui.PushID(childIndex++);
                        try
                        {
                            using var disabled = ImGuiScopes.Disabled(canSelect?.Invoke(type) == false);
                            if (Gui.Selectable(type.DisplayValue, type.IsMatchingValue(current!))) select(type);
                        }
                        finally { Gui.PopID(); }
                    }
                }
                finally { Gui.EndMenu(); }
            }
            else
            {
                using var disabled = ImGuiScopes.Disabled(canSelect?.Invoke(entry) == false);
                if (Gui.Selectable(entry.DisplayValue, entry.IsMatchingValue(current!))) select(entry);
            }
            }
            finally { Gui.PopID(); }
        }
    }
    internal static string Label(IReadOnlyList<AbstractNodeEntry> entries, object? current)
    {
        foreach (var entry in entries)
        {
            if (entry is AbstractNodeTypeGroup group)
            { var match = group.Types.FirstOrDefault(type => type.IsMatchingValue(current!)); if (match != null) return match.DisplayValue; }
            else if (entry.IsMatchingValue(current!)) return entry.DisplayValue;
        }
        return current == null ? "None" : Stride.Core.DisplayAttribute.GetDisplayName(current.GetType());
    }
}
