namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    internal object? ReadRuntimeComponentValueVerification(ComponentField field)
        => InspectionViewport?.ReadRuntimeComponentValueVerification(field);
    internal object? CaptureRuntimeReferenceMismatchesVerification()
        => document == null ? null : InspectionViewport?.CaptureRuntimeReferenceMismatchesVerification(document);
}

internal sealed partial class EngineViewport
{
    internal object? ReadRuntimeComponentValueVerification(ComponentField field)
    {
        if (!runtimeEntities.TryGetValue(field.Component.Entity.Id, out var owner)) return null;
        var component = owner.Components.FirstOrDefault(candidate => candidate.Id == field.Component.Id);
        return component == null ? null : field.ReadRuntime(component);
    }
    internal object CaptureRuntimeReferenceMismatchesVerification(EntityHierarchyDocument document)
    {
        object? Map(object? value) => value switch
        {
            Stride.Engine.Entity entity => runtimeEntities.GetValueOrDefault(entity.Id),
            Stride.Engine.EntityComponent component => runtimeEntities.TryGetValue(component.Entity.Id, out var owner)
                ? owner.Components.FirstOrDefault(candidate => candidate.Id == component.Id) : null,
            _ => null,
        };
        static object? Identity(object? value) => value switch
        {
            Stride.Engine.Entity entity => new { kind = "Entity", id = entity.Id, owner = entity.Id, type = entity.GetType().FullName },
            Stride.Engine.EntityComponent component => new { kind = "Component", id = component.Id, owner = component.Entity.Id, type = component.GetType().FullName },
            _ => null,
        };
        var mismatches = new List<object>();
        foreach (var field in document.GetEntityReferenceFields())
        {
            var source = field.Node.Retrieve();
            var actual = ReadRuntimeComponentValueVerification(field);
            var canonicalComponent = document.Asset.Hierarchy.Parts[field.Component.Entity.Id].Entity.Components
                .Single(component => component.Id == field.Component.Id);
            var liveSource = field.ReadRuntime(canonicalComponent);
            var liveSources = liveSource is System.Collections.IEnumerable liveSequence && liveSource is not string
                ? liveSequence.Cast<object?>().ToArray() : new[] { liveSource };
            var sources = source is System.Collections.IEnumerable sequence && source is not string
                ? sequence.Cast<object?>().ToArray() : new[] { source };
            var actuals = actual is System.Collections.IEnumerable runtimeSequence && actual is not string
                ? runtimeSequence.Cast<object?>().ToArray() : new[] { actual };
            var expected = sources.Select(Map).ToArray();
            if (expected.Length == actuals.Length && expected.Zip(actuals).All(pair => ReferenceEquals(pair.First, pair.Second))) continue;
            mismatches.Add(new { field.Name, component = field.Component.Id, owner = field.Component.Entity.Id,
                source = sources.Select(Identity).ToArray(), expected = expected.Select(Identity).ToArray(), actual = actuals.Select(Identity).ToArray(),
                liveSource = liveSources.Select(Identity).ToArray(),
                fieldComponentIsCanonical = ReferenceEquals(field.Component, canonicalComponent),
                nodeValueIsLiveSource = ReferenceEquals(source, liveSource),
                runtimeAliasesAuthoredCollection = ReferenceEquals(liveSource, actual),
                authoredCanonical = liveSources.Select(value => value switch
                {
                    Stride.Engine.Entity entity => document.Asset.Hierarchy.Parts.TryGetValue(entity.Id, out var part) && ReferenceEquals(part.Entity, entity),
                    Stride.Engine.EntityComponent component => document.Asset.Hierarchy.Parts.TryGetValue(component.Entity.Id, out var part)
                        && part.Entity.Components.Any(candidate => ReferenceEquals(candidate, component)),
                    _ => value == null,
                }).ToArray(),
                runtimeCanonicalAgainstLive = liveSources.Select(Map).Zip(actuals).Select(pair => ReferenceEquals(pair.First, pair.Second)).ToArray(),
                structureVersion = document.StructureVersion, synchronizedStructureVersion,
                componentReplacementVersion = document.ComponentReplacementVersion(field.Component.Id),
                runtimeReplacementVersion = runtimeComponentReplacementVersions.GetValueOrDefault(field.Component.Id),
                canonicalIdentity = expected.Zip(actuals).Select(pair => ReferenceEquals(pair.First, pair.Second)).ToArray() });
        }
        return mismatches;
    }
}
