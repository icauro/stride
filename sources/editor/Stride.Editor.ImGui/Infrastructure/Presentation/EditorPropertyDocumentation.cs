using System.Reflection;
using Hexa.NET.ImGui;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Quantum;
using Stride.Core.Reflection;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

/// <summary>Displays only original user documentation for the canonical member currently under the pointer.</summary>
internal sealed class EditorPropertyDocumentation(Func<Assembly, string?> resolveLocation)
{
    internal AssetUserDocumentation Store { get; } = new(resolveLocation);
    internal void Show(IMemberDescriptor member, Type? rootType = null)
    {
        if (!Hovered()) return;
        ShowText(Store.GetMemberDocumentation(member, rootType));
    }
    internal void Show(IGraphNode node, Type? rootType = null)
    { if (node is IMemberNode member) Show(member.MemberDescriptor, rootType); }
    internal void Show(PropertyKey key)
    {
        if (!Hovered()) return;
        ShowText(Store.GetPropertyKeyDocumentation(key));
    }
    internal void Show(MemberInfo member, Type? rootType = null)
    {
        if (!Hovered()) return;
        ShowText(Store.GetMemberDocumentation(member, rootType));
    }
    private static bool Hovered() => Gui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled | ImGuiHoveredFlags.DelayNormal);
    private static void ShowText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !Gui.BeginTooltip()) return;
        try
        {
            Gui.PushTextWrapPos(Gui.GetFontSize() * 35);
            try { Gui.TextUnformatted(text); } finally { Gui.PopTextWrapPos(); }
        }
        finally { Gui.EndTooltip(); }
    }
}
