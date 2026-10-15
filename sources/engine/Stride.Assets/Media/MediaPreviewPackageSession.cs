using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Stride.Assets.Effect;
using Stride.Core.Assets;
using Stride.Core.Assets.Analysis;
using Stride.Core.Diagnostics;
using Stride.Core.IO;

namespace Stride.Assets.Media;

/// <summary>Reconstructs captured source-package ownership without evaluating or saving projects.</summary>
public static class MediaPreviewPackageSession
{
    public static PackageSession Create(MediaPreviewAssetInput[] inputs, string outputDirectory, ILogger log,
        Action<MediaPreviewAssetInput>? verify = null)
    {
        var session = new PackageSession();
        try
        {
            string OwnerKey(MediaPreviewAssetInput input) => !string.IsNullOrEmpty(input.SourcePackagePath)
                ? Path.GetFullPath(input.SourcePackagePath) : "namespace:" + (input.AssetNamespace ?? "");
            var owners = inputs.GroupBy(OwnerKey, StringComparer.OrdinalIgnoreCase).ToArray();
            var packages = new Dictionary<string, Package>(StringComparer.OrdinalIgnoreCase);
            foreach (var owner in owners)
            {
                var namespaces = owner.Select(input => input.AssetNamespace).Distinct(StringComparer.Ordinal).ToArray();
                if (namespaces.Length != 1) throw new InvalidDataException("A captured source package has conflicting namespaces: " + owner.Key);
                var first = owner.First();
                var package = new Package { AssetNamespace = first.AssetNamespace,
                    FullPath = string.IsNullOrEmpty(first.SourcePackagePath)
                        ? Path.Combine(outputDirectory, "preview-package-" + packages.Count + ".sdpkg") : first.SourcePackagePath };
                session.Projects.Add(new StandalonePackage(package) { AssetNamespace = first.AssetNamespace });
                packages.Add(owner.Key, package);
            }
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var urls = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var input in inputs)
            {
                verify?.Invoke(input);
                if (!ids.Add(input.Id)) throw new InvalidDataException("A captured asset has multiple package owners: " + input.Id);
                var package = packages[OwnerKey(input)];
                var local = string.IsNullOrEmpty(input.LocalUrl) ? package.Container.Unqualify(new UFile(input.Url)).ToString() : input.LocalUrl;
                if (new UFile(local).IsAbsolute) throw new InvalidDataException("A captured asset has no valid package-local location: " + input.Id);
                var qualified = package.Container.Qualify(new UFile(local)).ToString();
                if (input.Url.StartsWith("/", StringComparison.Ordinal) && qualified != input.Url)
                    throw new InvalidDataException("A captured asset URL differs from its package namespace: " + input.Id);
                if (urls.TryGetValue(qualified, out var previous) && previous != input.Id)
                    throw new InvalidDataException("Captured assets collide at their canonical URL: " + qualified);
                urls[qualified] = input.Id;
                var asset = AssetFileSerializer.Load<Asset>(input.Snapshot, log, input.AssetNamespace).Asset;
                var id = new AssetId(Guid.Parse(input.Id));
                if (asset is SourceCodeAsset) asset.Id = id;
                if (asset.Id != id || asset is Stride.Core.Yaml.IUnloadable || UnloadableObjectRemover.Discover(asset).Count != 0)
                    throw new InvalidDataException("A captured asset has mismatched identity or unresolved native types: " + input.Id);
                var item = new AssetItem(local, asset)
                { AlternativePath = new UFile(asset is EffectShaderAsset ? input.Snapshot : input.AssetPath) };
                package.Assets.Add(item);
                if (item.Location.ToString() != qualified)
                    throw new InvalidDataException("A reconstructed asset changed its canonical URL: " + input.Id);
            }
            foreach (var owner in owners)
            {
                var package = packages[owner.Key];
                foreach (var key in owner.SelectMany(input => input.PackageDependencies).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
                    if (packages.TryGetValue(key, out var dependency) && !ReferenceEquals(package, dependency))
                        package.Container.FlattenedDependencies.Add(new Dependency(dependency));
            }
            // Match normal package analysis: detached YAML stores relative physical
            // sources. Compilers such as Heightmap consume their UPaths directly,
            // so resolve them against each retained authored asset origin before
            // executing commands in the private worker's working directory.
            AssetAnalysis.Run(session.Packages.SelectMany(package => package.Assets), log,
                new AssetAnalysisParameters { IsProcessingUPaths = true, ConvertUPathTo = UPathType.Absolute });
            return session;
        }
        catch { session.Dispose(); throw; }
    }
}
