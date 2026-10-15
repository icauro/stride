using System.Collections;
using Stride.Core.Serialization.Contents;
using Stride.Engine;
using Stride.Graphics;
using Stride.Particles.Components;
using Stride.Particles.Materials;
using Stride.Rendering;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;
using Stride.Rendering.Skyboxes;
using Stride.Rendering.Sprites;
using Stride.SpriteStudio.Runtime;
using Stride.UI;
using Stride.UI.Controls;

namespace Stride.Editor.ImGui;

internal interface IRuntimeTextureConsumer
{
    bool UsesRuntimeTexture(Texture texture);
}

internal static partial class CompiledTextureConsumers
{
    internal static bool UsesTexture(object? value, Texture texture, HashSet<object>? visited = null)
    {
        if (ReferenceEquals(value, texture)) return true;
        if (value == null) return false;
        visited ??= new HashSet<object>(ReferenceEqualityComparer.Instance);
        if (!visited.Add(value)) return false;
        bool Uses(object? child) => UsesTexture(child, texture, visited);
        switch (value)
        {
            case ContentManager cache:
                return cache.GetLoadedAssetsSnapshot().Any(Uses);
            case Scene scene:
                return scene.Entities.Any(Uses) || scene.Children.Any(Uses);
            case Entity entity:
                return entity.Components.Any(Uses) || entity.Transform.Children.Any(child => Uses(child.Entity));
            case ModelComponent component:
                return Uses(component.Model) || component.Materials.Any(pair => Uses(pair.Value));
            case Model model:
                return model.Materials.Any(instance => Uses(instance.Material));
            case Material material:
                return material.Passes.Any(pass => Uses(pass.Parameters));
            case ParameterCollection parameters:
                return parameters.ObjectValues != null && parameters.ObjectValues.Any(Uses);
            case BackgroundComponent background:
                return Uses(background.Texture);
            case SpriteComponent spriteComponent:
                return Uses(spriteComponent.SpriteProvider);
            case SpriteFromTexture direct:
                return Uses(direct.Texture);
            case SpriteFromSheet fromSheet:
                return Uses(fromSheet.Sheet);
            case SpriteSheet sheet:
                return sheet.Sprites.Any(Uses);
            case Sprite sprite:
                return Uses(sprite.Texture);
            case SpriteFont font:
                return font.Textures != null && font.Textures.Any(Uses);
            case SpriteStudioComponent studio:
                return Uses(studio.Sheet) || Uses(studio.CurrentSheet);
            case SpriteStudioSheet studioSheet:
                return studioSheet.Sprites != null && studioSheet.Sprites.Any(Uses);
            case UIComponent ui:
                return Uses(ui.Page);
            case UIPage page:
                return Uses(page.RootElement);
            case UIElement element:
                return element is ContentControl control && Uses(control.Content)
                    || element is TextBlock text && Uses(text.Font) || element is EditText edit && Uses(edit.Font)
                    || ElementSpriteProviders(element).Any(Uses) || element.VisualChildren.Any(Uses);
            case ParticleSystemComponent particles:
                return particles.ParticleSystem.Emitters.Any(emitter => Uses(emitter.Material));
            case ParticleMaterial particlesMaterial:
                return Uses(particlesMaterial.Parameters) || particlesMaterial is ParticleMaterialComputeColor color && Uses(color.ComputeColor);
            case IComputeNode compute:
                return compute is ComputeTextureBase computeTexture && Uses(computeTexture.Texture) || compute.GetChildren().Any(Uses);
            case Skybox skybox:
                return Uses(skybox.Parameters) || Uses(skybox.DiffuseLightingParameters) || Uses(skybox.SpecularLightingParameters);
            case EntityComponent otherComponent:
                var references = new ImportedRuntimeReferences(); references.Visit(otherComponent);
                return references.References.Any(reference => Uses(reference.Value));
            case IDictionary dictionary:
                foreach (var child in dictionary.Values) if (Uses(child)) return true;
                return false;
            case IEnumerable collection when value is not string:
                foreach (var child in collection) if (Uses(child)) return true;
                return false;
            default:
                return false;
        }
    }
}
