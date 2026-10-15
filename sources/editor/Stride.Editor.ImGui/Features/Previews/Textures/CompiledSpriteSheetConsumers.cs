using Stride.Core.Serialization;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering.Sprites;
using Stride.UI;
using Stride.UI.Controls;

namespace Stride.Editor.ImGui;

/// <summary>Rebinds real compiled sheet references while preserving provider animation state.</summary>
internal static class CompiledSpriteSheetConsumers
{
    internal static List<CompiledTextureConsumers.Change> Prepare(object? value, string url, SpriteSheet sheet,
        HashSet<object>? visited = null, List<CompiledTextureConsumers.Change>? changes = null)
    {
        visited ??= new HashSet<object>(ReferenceEqualityComparer.Instance);
        changes ??= [];
        if (value == null || !visited.Add(value)) return changes;
        void Visit(object? child) => Prepare(child, url, sheet, visited, changes);
        switch (value)
        {
            case Scene scene:
                foreach (var entity in scene.Entities) Visit(entity);
                foreach (var child in scene.Children) Visit(child);
                break;
            case Entity entity:
                foreach (var component in entity.Components) Visit(component);
                foreach (var child in entity.Transform.Children) Visit(child.Entity);
                break;
            case SpriteComponent spriteComponent:
                Visit(spriteComponent.SpriteProvider);
                break;
            case SpriteFromSheet provider when provider.Sheet != null
                && !ReferenceEquals(provider.Sheet, sheet)
                && string.Equals(AttachedReferenceManager.GetUrl(provider.Sheet), url, StringComparison.Ordinal):
                var previous = provider.Sheet;
                changes.Add(new(() => provider.Sheet = sheet, () => provider.Sheet = previous));
                break;
            case UIComponent uiComponent:
                Visit(uiComponent.Page);
                break;
            case UIPage page:
                Visit(page.RootElement);
                break;
            case UIElement element:
                foreach (var spriteProvider in CompiledTextureConsumers.ElementSpriteProviders(element)) Visit(spriteProvider);
                if (element is ContentControl contentControl) Visit(contentControl.Content);
                foreach (var child in element.VisualChildren) Visit(child);
                break;
        }
        return changes;
    }
}
