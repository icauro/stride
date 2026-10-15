using Stride.Engine;
using Stride.Core.Reflection;
using Stride.Graphics;
using Stride.Particles.Components;
using Stride.Particles.Materials;
using Stride.Rendering;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;
using Stride.Rendering.Sprites;
using Stride.UI;
using Stride.UI.Controls;

namespace Stride.Editor.ImGui;

internal static partial class CompiledTextureConsumers
{
    internal static bool SupportsContent(object value) => value is Model or Material or SpriteSheet or Sprite
        or ISpriteProvider or UIPage or UIElement or SpriteComponent or ModelComponent
        or BackgroundComponent or UIComponent or ParticleSystemComponent or EntityComponent;

    internal static void ReplaceContent(object value, string url, Texture texture)
    {
        var changes = new List<Change>();
        PrepareAdditionalConsumer(value, url, texture, changes, new HashSet<object>(ReferenceEqualityComparer.Instance));
        Apply(changes);
    }

    internal static void PrepareAdditionalConsumer(object? value, string url, Texture texture,
        List<Change> changes, HashSet<object> visited)
    {
        if (value == null || !visited.Add(value)) return;
        void Visit(object? child) => PrepareAdditionalConsumer(child, url, texture, changes, visited);
        switch (value)
        {
            case ModelComponent modelComponent:
                Visit(modelComponent.Model);
                foreach (var pair in modelComponent.Materials) Visit(pair.Value);
                break;
            case Model model:
                foreach (var instance in model.Materials) Visit(instance.Material);
                break;
            case Material material:
                PrepareMaterial(material, url, texture, changes);
                break;
            case BackgroundComponent background when Matches(background.Texture, url):
                var previousBackground = background.Texture;
                changes.Add(new(() => background.Texture = texture, () => background.Texture = previousBackground));
                break;
            case SpriteComponent spriteComponent:
                Visit(spriteComponent.SpriteProvider);
                break;
            case SpriteFromTexture direct when Matches(direct.Texture, url):
                var previousDirect = direct.Texture;
                changes.Add(new(() => direct.Texture = texture, () => direct.Texture = previousDirect));
                break;
            case SpriteFromSheet fromSheet:
                Visit(fromSheet.Sheet);
                break;
            case SpriteSheet sheet:
                foreach (var sprite in sheet.Sprites) Visit(sprite);
                break;
            case Sprite sprite when Matches(sprite.Texture, url):
                // Keep authored atlas region/center/borders and animation frames intact.
                var previousSprite = sprite.Texture;
                changes.Add(new(() => sprite.Texture = texture, () => sprite.Texture = previousSprite));
                break;
            case UIComponent ui:
                Visit(ui.Page);
                break;
            case UIPage page:
                Visit(page.RootElement);
                break;
            case UIElement element:
                if (element is ContentControl contentControl) Visit(contentControl.Content);
                foreach (var provider in ElementSpriteProviders(element)) Visit(provider);
                foreach (var child in element.VisualChildren) Visit(child);
                break;
            case ParticleSystemComponent system:
                foreach (var emitter in system.ParticleSystem.Emitters) Visit(emitter.Material);
                break;
            case ParticleMaterial particleMaterial:
                PrepareParameters(particleMaterial.Parameters, url, texture, changes);
                if (particleMaterial is ParticleMaterialComputeColor color) Visit(color.ComputeColor);
                break;
            case IComputeNode compute:
                if (compute is ComputeTextureBase computeTexture && Matches(computeTexture.Texture, url))
                {
                    var previousCompute = computeTexture.Texture;
                    changes.Add(new(() => computeTexture.Texture = texture, () => computeTexture.Texture = previousCompute));
                }
                foreach (var child in compute.GetChildren()) Visit(child);
                break;
            case EntityComponent component:
                // Use the native serialized schema, including collection/dictionary paths,
                // rather than inspecting arbitrary plugin properties or resource internals.
                var references = new ImportedRuntimeReferences();
                references.Visit(component);
                foreach (var reference in references.References)
                {
                    if (reference.Value is not Texture previous)
                    {
                        Visit(reference.Value);
                        continue;
                    }
                    if (!Matches(previous, url) || !reference.CanWrite
                        || !reference.StorageType.IsInstanceOfType(texture)) continue;
                    var path = reference.Path;
                    changes.Add(new(() =>
                    {
                        if (!path.Apply(component, MemberPathAction.ValueSet, texture))
                            throw new InvalidOperationException("A runtime texture reference could not be updated.");
                    }, () =>
                    {
                        if (!path.Apply(component, MemberPathAction.ValueSet, previous))
                            throw new InvalidOperationException("A runtime texture reference could not be restored.");
                    }));
                }
                break;
        }
    }

    internal static IEnumerable<ISpriteProvider?> ElementSpriteProviders(UIElement element)
    {
        if (element is ContentDecorator decorator) yield return decorator.BackgroundImage;
        if (element is ImageElement image) yield return image.Source;
        if (element is Button button)
        {
            yield return button.PressedImage; yield return button.NotPressedImage; yield return button.MouseOverImage;
        }
        if (element is ToggleButton toggle)
        {
            yield return toggle.CheckedImage; yield return toggle.UncheckedImage; yield return toggle.IndeterminateImage;
            yield return toggle.CheckedMouseOverImage; yield return toggle.UncheckedMouseOverImage; yield return toggle.IndeterminateMouseOverImage;
        }
        if (element is EditText edit)
        {
            yield return edit.ActiveImage; yield return edit.InactiveImage; yield return edit.MouseOverImage;
        }
        if (element is Slider slider)
        {
            yield return slider.TrackBackgroundImage; yield return slider.TrackForegroundImage;
            yield return slider.ThumbImage; yield return slider.MouseOverThumbImage; yield return slider.TickImage;
        }
    }
}
