using Stride.Engine;
using Stride.Graphics;
using Stride.UI;
using Stride.UI.Controls;
using Stride.Core.Serialization;

namespace Stride.Editor.ImGui;

internal static class CompiledFontConsumers
{
    public static void Prepare(object? value, string url, SpriteFont replacement, List<CompiledTextureConsumers.Change> changes, HashSet<object> visited)
    {
        if (value == null || !visited.Add(value)) return;
        switch (value)
        {
            case UIComponent ui: Prepare(ui.Page, url, replacement, changes, visited); break;
            case UIPage page: Prepare(page.RootElement, url, replacement, changes, visited); break;
            case UIElement element:
                if (element is TextBlock text && text.Font is { } previous && AttachedReferenceManager.GetUrl(previous) == url)
                    changes.Add(new(() => text.Font = replacement, () => text.Font = previous));
                if (element is EditText edit && edit.Font is { } previousEdit && AttachedReferenceManager.GetUrl(previousEdit) == url)
                    changes.Add(new(() => edit.Font = replacement, () => edit.Font = previousEdit));
                if (element is ContentControl control) Prepare(control.Content, url, replacement, changes, visited);
                foreach (var child in element.VisualChildren) Prepare(child, url, replacement, changes, visited);
                break;
        }
    }
    public static bool Uses(object? value, SpriteFont font, HashSet<object> visited)
    {
        if (value == null || !visited.Add(value)) return false;
        return value switch
        {
            UIComponent ui => Uses(ui.Page, font, visited),
            UIPage page => Uses(page.RootElement, font, visited),
            UIElement element => element is TextBlock text && ReferenceEquals(text.Font, font)
                || element is EditText edit && ReferenceEquals(edit.Font, font)
                || element is ContentControl control && Uses(control.Content, font, visited)
                || element.VisualChildren.Any(child => Uses(child, font, visited)),
            _ => false
        };
    }
}
