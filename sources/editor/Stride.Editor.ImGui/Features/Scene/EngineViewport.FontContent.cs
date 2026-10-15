using Stride.Graphics;

namespace Stride.Editor.ImGui;

internal sealed partial class EngineViewport
{
    internal void ReplaceRuntimeFont(string url, SpriteFont font, EntityHierarchyDocument? document)
    {
        var changes = new List<CompiledTextureConsumers.Change>(); var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var entity in ImportRuntimeEntities(document))
            foreach (var component in entity.Components) CompiledFontConsumers.Prepare(component, url, font, changes, visited);
        foreach (var cached in componentContent.Values) CompiledFontConsumers.Prepare(cached, url, font, changes, visited);
        CompiledTextureConsumers.Apply(changes);
        var replacements = componentContent.Where(pair => pair.Key.Url == url && pair.Value is SpriteFont)
            .Select(pair => new ImportedContent(pair.Key.Type, url, font)).Append(new(typeof(SpriteFont), url, font)).DistinctBy(value => value.Type).ToArray();
        try { ReplaceRuntimeImportedContent(replacements, document); }
        catch (Exception failure)
        {
            var failures = new List<Exception> { failure };
            for (var index = changes.Count - 1; index >= 0; index--) try { changes[index].Undo(); } catch (Exception error) { failures.Add(error); }
            if (failures.Count > 1) throw new AggregateException("Font consumers could not be restored.", failures);
            throw;
        }
    }
    internal bool UsesRuntimeFont(SpriteFont font, EntityHierarchyDocument? document)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        return componentContent.Values.Any(value => ReferenceEquals(value, font) || CompiledFontConsumers.Uses(value, font, visited))
            || ImportRuntimeEntities(document).Any(entity => entity.Components.Any(component => CompiledFontConsumers.Uses(component, font, visited)))
            || UsesImportedContent([font], document);
    }
}
