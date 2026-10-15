namespace Stride.Assets.Presentation;

// The linked native scene-settings contract reads this one toolkit-specific
// dictionary. Reuse the ImGui registry rather than loading the WPF plugin.
internal static class StrideDefaultAssetsPlugin
{
    public static IReadOnlyDictionary<Type, Type> GizmoTypeDictionary
    {
        get
        {
            using var registry = new Stride.Editor.ImGui.SceneComponentGizmoRegistry();
            return registry.Families.ToDictionary(family => family.ComponentType, family => family.GeometryType);
        }
    }
}
