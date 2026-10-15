using Stride.Assets.Scripts;
using Stride.Core.Assets;
using Stride.Core.Yaml;
using Stride.Core.Diagnostics;
using Stride.Core.IO;
using Stride.Core.Assets.Serializers;
using Stride.Core.Assets.Yaml;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal sealed record VisualScriptCompilationSnapshot(AssetItem Item, long Revision, string ProjectPath, string GeneratedPath, VisualScriptCompilerResult Result)
{
    internal object? ReferenceEvidence { get; init; }
    internal int ObjectReferenceCount { get; init; }
}

internal sealed partial class VisualScriptDocument
{
    internal VisualScriptCompilationSnapshot CaptureCompilation(AssetItem item)
    {
        if (item.Id != Asset.Id) throw new InvalidOperationException("The compiler item has another visual-script identity.");
        // Untouched documents also require the actual native graph definition
        // to prepare endpoint reference metadata before snapshot serialization.
        _ = AuthoringGraph;
        var yaml = CaptureAssemblyReload(item);
        using var stream = new MemoryStream(yaml.Yaml, false);
        var log = new LoggerResult();
        var restored = AssetFileSerializer.Load<Asset>(stream, new UFile(Path), log, yaml.Namespace);
        if (restored.Asset is not VisualScriptAsset asset || asset.Id != Asset.Id || log.HasErrors)
            throw new InvalidDataException("The serialized visual-script compiler snapshot could not be restored.");
        var snapshotItem = item.Clone(keepPackage: true, newAsset: asset);
        var project = item.Package.Container as SolutionProject ?? throw new InvalidOperationException("The visual script has no actual source project.");
        object? referenceEvidence = null;
        if (Environment.GetCommandLineArgs().Contains("--ui-test"))
        {
            var references = restored.YamlMetadata.RetrieveMetadata(AssetObjectSerializerBackend.ObjectReferencesKey);
            var targets = references == null ? null : FixupObjectReferences.CollectReferenceableObjects(asset, references, true, log);
            var paths = references?.Select(reference =>
            {
                var path = reference.Key.ToMemberPath(asset);
                var current = path.GetValue(asset);
                var target = targets!.GetValueOrDefault(reference.Value);
                return new { path = reference.Key.ToString(), id = reference.Value, currentType = current?.GetType().FullName,
                    targetType = target?.GetType().FullName, currentOwner = (current as Slot)?.Owner?.Id,
                    targetOwner = (target as Slot)?.Owner?.Id, actualAlias = ReferenceEquals(current, target) };
            }).ToArray();
            // Exercise native path application only on a separate diagnostic
            // deserialize. The actual compiler snapshot remains untouched.
            using var diagnosticStream = new MemoryStream(yaml.Yaml, false);
            var diagnostic = AssetFileSerializer.Load<Asset>(diagnosticStream, new UFile(Path), log, yaml.Namespace);
            var diagnosticReferences = diagnostic.YamlMetadata.RetrieveMetadata(AssetObjectSerializerBackend.ObjectReferencesKey);
            var application = new List<object>();
            if (diagnosticReferences != null)
            {
                var diagnosticTargets = FixupObjectReferences.CollectReferenceableObjects(diagnostic.Asset, diagnosticReferences, true, log);
                foreach (var reference in diagnosticReferences)
                {
                    var path = reference.Key.ToMemberPath(diagnostic.Asset);
                    var target = diagnosticTargets.GetValueOrDefault(reference.Value);
                    var before = path.GetValue(diagnostic.Asset);
                    var applied = target != null && path.Apply(diagnostic.Asset, MemberPathAction.ValueSet, target);
                    application.Add(new { path = reference.Key.ToString(), id = reference.Value, targetFound = target != null,
                        beforeAlias = ReferenceEquals(before, target), applied, afterAlias = ReferenceEquals(path.GetValue(diagnostic.Asset), target),
                        targetOwner = (target as Slot)?.Owner?.Id });
                }
            }
            referenceEvidence = new { metadataCount = references?.Count, paths, diagnosticApplication = application };
        }
        return new(snapshotItem, EditRevision, project.FullPath.ToOSPath(), item.GetGeneratedAbsolutePath().ToOSPath(), asset.Compile(snapshotItem))
        { ReferenceEvidence = referenceEvidence,
            ObjectReferenceCount = restored.YamlMetadata.RetrieveMetadata(AssetObjectSerializerBackend.ObjectReferencesKey)?.Count ?? 0 };
    }
}
