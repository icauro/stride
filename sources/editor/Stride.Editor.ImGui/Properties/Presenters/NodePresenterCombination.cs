using Stride.Core.Annotations;

using Stride.Core;
using Stride.Core.Presentation.Quantum;
using Stride.Core.Presentation.Quantum.Presenters;

namespace Stride.Editor.ImGui;

/// <summary>The original property-grid combination rules over actual native presenters.</summary>
internal static class NodePresenterCombination
{
    public static IEnumerable<List<INodePresenter>> CombineChildren(IReadOnlyCollection<INodePresenter> parents)
    {
        var groups = new Dictionary<string, List<INodePresenter>>();
        foreach (var parent in parents)
            foreach (var child in parent.Children)
            {
                if (!groups.TryGetValue(child.CombineKey, out var group)) groups.Add(child.CombineKey, group = []);
                group.Add(child);
            }
        return groups.Values.Where(group => group.Count == parents.Count);
    }
    public static bool CanConstruct(IReadOnlyList<INodePresenter> presenters)
    {
        Type? type = null;
        foreach (var presenter in presenters)
        {
            if (presenter is MemberNodePresenter member && member.MemberAttributes.OfType<DisplayAttribute>().Any(display => !display.Browsable)) return false;
            if (type == null) type = presenter.Type;
            else if (type != presenter.Type && type.IsAssignableFrom(presenter.Type)) type = presenter.Type;
            else if (type != presenter.Type) return false;
        }
        return presenters.Count != 0;
    }
    public static IEnumerable<INodePresenterCommand> Commands(IReadOnlyCollection<INodePresenter> presenters)
    {
        var counts = new Dictionary<INodePresenterCommand, int>();
        foreach (var presenter in presenters)
            foreach (var command in presenter.Commands) counts[command] = counts.GetValueOrDefault(command) + 1;
        return counts.Where(pair => !(pair.Key.CombineMode == CombineMode.DoNotCombine && pair.Value > 1)
            && !(pair.Key.CombineMode == CombineMode.CombineOnlyForAll && pair.Value < presenters.Count)).Select(pair => pair.Key);
    }
}
