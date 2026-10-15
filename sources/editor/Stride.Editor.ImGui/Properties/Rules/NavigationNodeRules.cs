using System;
using Stride.Navigation;

namespace Stride.Assets.Presentation.NodePresenters.Rules;

internal static class NavigationNodeRules
{
    internal static string GroupName(NavigationMeshGroup? group) => group?.ToString() ?? "None";
    internal static bool BypassMember(Type ownerType, string name)
        => typeof(NavigationMeshGroup).IsAssignableFrom(ownerType) && name == nameof(NavigationMeshGroup.AgentSettings);
}
