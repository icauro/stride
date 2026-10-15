using Hexa.NET.ImGui;
using System.Runtime.CompilerServices;
using Stride.Core.Annotations;
using Stride.Core;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Keys;
using Stride.Core.Presentation.Quantum.Presenters;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

/// <summary>Expansion of actual native virtual category nodes during one property-grid draw.</summary>
internal sealed class NativePresenterCategories(Action<INodePresenter>? drawCommands = null, Func<INodePresenter, bool>? visible = null)
{
    private sealed class Expansion(bool value) { internal bool Value { get; } = value; }
    private readonly ConditionalWeakTable<INodePresenter, Expansion> expanded = new();
    internal static bool IsCategory(INodePresenter presenter)
        => presenter.AttachedProperties.TryGetValue(CategoryData.Key, out var category) && category;
    internal static string Identity(INodePresenter presenter)
    {
        var path = new Stack<string>();
        for (INodePresenter? node = presenter; node?.Parent != null; node = node.Parent) path.Push(node.CombineKey);
        return string.Join("/", path.Select(key => key.Length + ":" + key));
    }
    internal bool Before(INodePresenter presenter, bool includeSelf = true, bool includeMemberCategories = true)
    {
        var parents = new Stack<INodePresenter>();
        for (var parent = presenter.Parent; parent != null; parent = parent.Parent)
            if (IsCategory(parent) && (includeMemberCategories || parent is VirtualNodePresenter)) parents.Push(parent);
        foreach (var parent in parents) if (!Header(parent)) return false;
        if (includeSelf && IsCategory(presenter)) { Header(presenter); return false; }
        return true;
    }
    private bool Header(INodePresenter presenter)
    {
        if (expanded.TryGetValue(presenter, out var cached)) return cached.Value;
        if (!(visible?.Invoke(presenter) ?? presenter.IsVisible)) { expanded.Add(presenter, new(false)); return false; }
        var expand = presenter.AttachedProperties.TryGetValue(DisplayData.AutoExpandRuleKey, out var rule) ? rule : ExpandRule.Auto;
        if (expand is ExpandRule.Always or ExpandRule.Once) Gui.SetNextItemOpen(true, ImGuiCond.Once);
        var label = Stride.Core.Presentation.Core.Utils.SplitCamelCase(presenter.DisplayName);
        var value = Gui.CollapsingHeader(label + "###native-category/" + Identity(presenter));
        drawCommands?.Invoke(presenter);
        expanded.Add(presenter, new(value));
        return value;
    }
}
