using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;
using Stride.Assets.Effect;
using Stride.Editor.Thumbnails;
using Stride.Core.Assets;
using Stride.Core.Assets.Tracking;
using Stride.Assets.SpriteFont;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private NativeThumbnailCompilationSnapshot CaptureNativeThumbnail(AssetEntry entry, Type compiler)
    {
        var leases = new List<IDisposable>();
        var snapshots = new List<NativeThumbnailAssemblySnapshot>();
        var transferred = false;
        try
        {
            // Canonical ownership is retained while the real graph closure is
            // serialized. The worker receives detached immutable bytes only.
            if (authoring != null)
                foreach (var state in authoring.PublishedStates) leases.Add(state.AcquireView());
            var shaders = workspace!.AssetItems.Where(item => item.Asset is EffectShaderAsset).Select(item => item.Id).ToArray();
            var authored = CaptureAuthoredPreviewCompilation(entry, true, shaders);
            var assemblies = new Dictionary<string, Assembly>(StringComparer.Ordinal);
            var graphicsAssembly = typeof(Stride.Graphics.GraphicsDevice).Assembly;
            var videoAssembly = typeof(Stride.Video.VideoSystem).Assembly;
            var roslynWorkspaceAssembly = typeof(Microsoft.CodeAnalysis.Workspace).Assembly;
            var projectEvaluationModules = new HashSet<Assembly>
            {
                typeof(Stride.Core.Assets.Asset).Assembly,
                typeof(Stride.Assets.Entities.SceneAsset).Assembly,
                typeof(Stride.Core.Assets.Quantum.AssetNodeContainer).Assembly,
            };
            var graphicsPlatform = Stride.Graphics.GraphicsDevice.Platform;
            var frameworkDirectory = Path.GetFullPath(RuntimeEnvironment.GetRuntimeDirectory());
            foreach (var path in authored.Parameters.AssemblyPaths)
            {
                var name = AssemblyName.GetAssemblyName(path).FullName;
                var packageOwners = workspace!.Session.Packages.SelectMany(package => package.LoadedAssemblies)
                    .Where(owner => owner.Assembly != null && !string.IsNullOrEmpty(owner.Path)
                        && string.Equals(Path.GetFullPath(owner.Path), path, StringComparison.OrdinalIgnoreCase))
                    .Select(owner => owner.Assembly).Distinct().ToArray();
                if (packageOwners.Length > 1)
                    throw new InvalidDataException("The native thumbnail source path has multiple loaded canonical modules: " + path);
                var loaded = packageOwners.SingleOrDefault()
                    ?? AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(candidate => candidate.FullName == name
                        && !string.IsNullOrEmpty(candidate.Location)
                        && string.Equals(Path.GetFullPath(candidate.Location), path, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException("The native thumbnail source module is not loaded in the canonical editor session: " + name);
                if (loaded.FullName != name)
                    throw new InvalidDataException("The native thumbnail source identity differs from the retained canonical module: " + path);
                assemblies.TryAdd(loaded.FullName!, loaded);
            }
            assemblies[compiler.Assembly.FullName!] = compiler.Assembly;
            foreach (var root in assemblies.Values.ToArray()) Dependencies(root);
            var sourceHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(Encoding.UTF8.GetBytes(authored.Revision + "|" + compiler.AssemblyQualifiedName));
            foreach (var assembly in assemblies.Values.OrderBy(value => value.FullName, StringComparer.Ordinal))
            {
                var snapshot = NativeThumbnailAssemblyCertificates.Capture(assembly);
                hash.AppendData(Encoding.UTF8.GetBytes(snapshot.Certificate.Identity + "|" + snapshot.Certificate.ModuleVersionId + "|" + snapshot.Certificate.Sha256));
                snapshots.Add(snapshot);
            }
            foreach (var input in authored.Assets)
            {
                var item = workspace.Session.FindAsset(new AssetId(Guid.Parse(input.Input.Id)))
                    ?? throw new InvalidDataException("A captured thumbnail asset left its canonical session.");
                var actual = authoring?.Find(item.Id)?.Document is IAssetTagDocument retained ? retained.AuthoredAsset : item.Asset;
                if (item.Package.State < PackageState.AssetsReady || actual is Stride.Core.Yaml.IUnloadable
                    || UnloadableObjectRemover.Discover(actual).Count != 0)
                    throw new InvalidDataException("A thumbnail dependency is not ready or contains unresolved asset types: " + item.Id);
                if (actual is SourceCodeAsset) continue; // Its actual authored text is already detached in the staged snapshot.
                var sources = new SourceFilesCollector();
                foreach (var file in sources.GetSourceFiles(actual).Keys)
                    Certify(Path.GetFullPath(file.ToOSPath(), Path.GetDirectoryName(input.Input.AssetPath)!));
                if (actual.MainSource is { } main && !string.IsNullOrEmpty(main))
                    Certify(Path.GetFullPath(main.ToOSPath(), Path.GetDirectoryName(input.Input.AssetPath)!));
                if (actual is SpriteFontAsset or PrecompiledSpriteFontAsset)
                    foreach (var file in FontSourcePaths.Files(actual, input.Input.AssetPath)) Certify(file);
            }
            // The live host explicitly uses these engine shader files. A private
            // compiler cannot borrow its mounted EffectSystem or assume the user
            // asset shader closure contains editor-only effects such as
            // StrideEditorForwardShadingEffect. Capture immutable bytes and bind
            // them into the same generation certificate as managed providers.
            var shaderRoot = Path.Combine(AppContext.BaseDirectory, "Shaders", "Engine");
            var shaderSnapshots = new List<NativeThumbnailShaderSnapshot>();
            foreach (var path in Directory.EnumerateFiles(shaderRoot, "*", SearchOption.AllDirectories)
                         .Where(path => Path.GetExtension(path) is ".sdsl" or ".sdfx").Order(StringComparer.OrdinalIgnoreCase))
            {
                var bytes = File.ReadAllBytes(path);
                var sha256 = Convert.ToHexString(SHA256.HashData(bytes));
                var relative = Path.GetRelativePath(shaderRoot, path);
                hash.AppendData(Encoding.UTF8.GetBytes(relative + "|" + sha256));
                sourceHashes[path] = sha256;
                shaderSnapshots.Add(new(relative, bytes, sha256));
            }
            if (!shaderSnapshots.Any(source => Path.GetFileName(source.RelativePath) == "StrideEditorForwardShadingEffect.sdfx"))
                throw new InvalidDataException("The canonical editor shader closure is missing its native thumbnail effect.");
            var captured = new NativeThumbnailCompilationSnapshot(authored, compiler.AssemblyQualifiedName!, snapshots.ToArray(), sourceHashes,
                Convert.ToHexString(hash.GetHashAndReset()), shaderSnapshots.ToArray());
            transferred = true; return captured;
            void Certify(string path)
            {
                if (!File.Exists(path) || sourceHashes.ContainsKey(path)) return;
                using var content = File.OpenRead(path);
                var sourceHash = Convert.ToHexString(SHA256.HashData(content)); sourceHashes[path] = sourceHash;
                hash.AppendData(Encoding.UTF8.GetBytes(path + "|" + sourceHash));
            }
            void Dependencies(Assembly assembly)
            {
                foreach (var reference in assembly.GetReferencedAssemblies())
                {
                    // These built-in asset modules deliberately exclude MSBuild from
                    // their NuGet runtime contract. Native thumbnails use already
                    // captured Package/AssetItem graphs, not project evaluation.
                    // Importing the editor's Locator SDK here would add conflicting
                    // SDK NuGet modules to the standalone asset compiler. Custom
                    // provider/module references remain mandatory and certified.
                    if (projectEvaluationModules.Contains(assembly)
                        && reference.Name is "Microsoft.Build" or "Microsoft.Build.Framework"
                            or "Microsoft.Build.Utilities.Core" or "Microsoft.NET.StringTools") continue;
                    var loadedDependency = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(candidate => candidate.FullName == reference.FullName);
                    // Roslyn's optional SQLite persistence implementation is not a
                    // dependency of its shipped Workspaces NuGet runtime contract.
                    // Merely loading Workspaces for script types must not activate it
                    // in a thumbnail compiler. If the editor actually activated the
                    // module, certify it normally. A compiler/plugin's own SQLite
                    // reference is never excluded by this exact declaring-module check.
                    if (ReferenceEquals(assembly, roslynWorkspaceAssembly) && loadedDependency == null
                        && reference.Name is "SQLitePCLRaw.core" or "SQLitePCLRaw.batteries_v2") continue;
                    // Stride.Graphics and Stride.Video carry compile-time interop references
                    // for every native API, while GraphicsApiHostResolver stages
                    // only the selected implementation's dependencies. Those
                    // inactive graphics-only edges are not this worker's runtime
                    // closure. A plugin/compiler's own reference remains required
                    // and resolves normally; do not suppress its load failures.
                    if ((ReferenceEquals(assembly, graphicsAssembly) || ReferenceEquals(assembly, videoAssembly)) && (reference.Name switch
                    {
                        "Vortice.Vulkan" => graphicsPlatform != Stride.Graphics.GraphicsPlatform.Vulkan,
                        "Silk.NET.Direct3D12" => graphicsPlatform != Stride.Graphics.GraphicsPlatform.Direct3D12,
                        "Silk.NET.Direct3D11" => graphicsPlatform != Stride.Graphics.GraphicsPlatform.Direct3D11,
                        _ => false,
                    })) continue;
                    var dependency = loadedDependency ?? Assembly.Load(reference);
                    // Exclude only the actual running CLR framework directory.
                    // Namespace/name prefixes do not identify user/plugin modules.
                    if (!string.IsNullOrEmpty(dependency.Location)
                        && string.Equals(Path.GetDirectoryName(Path.GetFullPath(dependency.Location)) + Path.DirectorySeparatorChar,
                            frameworkDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase)) continue;
                    // Engine modules are certified too: the child rejects a
                    // different already-loaded native engine MVID, never substitutes it.
                    if (assemblies.TryAdd(dependency.FullName!, dependency)) Dependencies(dependency);
                }
            }
        }
        finally
        {
            try { if (!transferred) NativeThumbnailArtifactCleanup.DisposeAll(snapshots); }
            finally { NativeThumbnailArtifactCleanup.DisposeAll(leases.AsEnumerable().Reverse()); }
        }
    }
}
