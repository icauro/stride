using System.Security.Cryptography;
using System.Text.Json;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata;
using Stride.Assets.Models;
using Stride.Core.Assets;
using Stride.Core.Assets.Tracking;
using Stride.Core.Diagnostics;
using Stride.Core.IO;
using Stride.Core.Storage;

namespace Stride.Editor.ImGui;

internal sealed record AssetSourceImportResult(AssetItem[] Assets, Dictionary<string, string> SourceHashes, LoggerResult Log,
    Dictionary<string, ObjectId> ContentHashes, Dictionary<AssetId, string>? AssetSources = null,
    IReadOnlyDictionary<AssetId, AssetSourceImportResult>? Reimports = null);
internal sealed record AssetImportOptions(Guid? ImporterId = null, IReadOnlyDictionary<Type, bool>? Outputs = null,
    bool DeduplicateMaterials = true, AssetId? ReuseSkeleton = null);
internal sealed record AssetImportChoice(Guid Id, string Name, string Description, IReadOnlyDictionary<Type, bool> DefaultOutputs,
    Type[] RootTypes, Type[] AdditionalTypes, bool SupportsDeduplicateMaterials, bool SupportsReuseSkeleton);
internal sealed record AssetImportAssemblyContext(string Path, string Hash, string ModuleVersion);

internal static partial class AssetSourceImport
{
    private static string Root => Path.GetFullPath(Path.Combine(EditorUserDataPaths.OverrideRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stride"), "Editor.ImGui", "source-imports"));
    public static AssetImportChoice[] Choices(string file, Type? preferredType = null)
        => AssetRegistry.FindImporterForFile(file).Where(importer => preferredType == null || importer.RootAssetTypes.Contains(preferredType)).Select(importer =>
        {
            var parameters = importer.GetDefaultParameters(false);
            if (preferredType != null && preferredType != typeof(ModelAsset))
                foreach (var type in parameters.SelectedOutputTypes.Keys.ToArray()) parameters.SelectedOutputTypes[type] = type == preferredType;
            return new AssetImportChoice(importer.Id, importer.Name, importer.Description, new Dictionary<Type, bool>(parameters.SelectedOutputTypes),
                importer.RootAssetTypes.ToArray(), importer.AdditionalAssetTypes.ToArray(),
                importer is ModelAssetImporter && parameters.IsTypeSelectedForOutput<ModelAsset>(),
                importer is ModelAssetImporter && parameters.IsTypeSelectedForOutput<ModelAsset>());
        }).ToArray();
    internal static string CaptureBoundAssemblyPath(System.Reflection.Assembly assembly, string declaredPath)
    {
        if (assembly.IsDynamic) throw new InvalidDataException("Dynamic modules cannot be captured for native import.");
        // PackageLoadedAssembly.Path records the package's declared SDK file even when
        // Package reused a deployed host module. Capture the actual CLR binding instead.
        var path = Path.GetFullPath(string.IsNullOrEmpty(assembly.Location) ? declaredPath : assembly.Location);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var pe = new PEReader(stream); var metadata = pe.GetMetadataReader();
        var module = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
        if (System.Reflection.AssemblyName.GetAssemblyName(path).FullName != assembly.FullName || module != assembly.ManifestModule.ModuleVersionId)
            throw new IOException("The native importer assembly source differs from its actual loaded binding: " + path);
        return path;
    }
    public static AssetImportAssemblyContext[] CaptureAssemblies(IEnumerable<string> projectAssemblies)
        => AssemblyClosure(ImporterAssemblyRoots(projectAssemblies))
            .Where(path => !string.IsNullOrEmpty(path)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).Select(path =>
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var pe = new PEReader(stream); var metadata = pe.GetMetadataReader();
                var module = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
                var loaded = AppDomain.CurrentDomain.GetAssemblies().Where(assembly => !assembly.IsDynamic
                    && string.Equals(assembly.Location, path, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (loaded.Length > 0 && loaded.Any(assembly => assembly.ManifestModule.ModuleVersionId != module))
                    throw new IOException("An importer assembly changed on disk before import: " + path);
                return new AssetImportAssemblyContext(path, Hash(path), module.ToString());
            }).ToArray();

    private static IEnumerable<string> ImporterAssemblyRoots(IEnumerable<string> projectAssemblies)
    {
        // A loaded package also owns build-tool assemblies. Only native importer and
        // asset registrations belong to the import worker's runtime context.
        var registered = AssetRegistry.RegisteredImporters.SelectMany(importer =>
            importer.RootAssetTypes.Concat(importer.AdditionalAssetTypes).Select(type => type.Assembly)
                .Append(importer.GetType().Assembly))
            .Concat(AssetRegistry.GetPublicTypes().Select(type => type.Assembly)).Distinct().ToArray();
        var projectPaths = projectAssemblies.ToArray();
        foreach (var assembly in registered)
        {
            var declared = string.IsNullOrEmpty(assembly.Location)
                ? projectPaths.SingleOrDefault(path => System.Reflection.AssemblyName.GetAssemblyName(path).FullName == assembly.FullName)
                : assembly.Location;
            if (string.IsNullOrEmpty(declared))
                throw new InvalidDataException("The registered importer or asset module has no capturable source: " + assembly.FullName);
            yield return CaptureBoundAssemblyPath(assembly, declared);
        }
    }

    private static IEnumerable<string> AssemblyClosure(IEnumerable<string> roots)
    {
        var pending = new Queue<string>(roots.Where(path => !string.IsNullOrEmpty(path)).Select(Path.GetFullPath));
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var runtime = Path.GetFullPath(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());
        var loaded = AppDomain.CurrentDomain.GetAssemblies().Where(assembly => !assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location)).ToArray();
        while (pending.TryDequeue(out var path))
        {
            if (!visited.Add(path)) continue;
            yield return path;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var pe = new PEReader(stream); var metadata = pe.GetMetadataReader();
            foreach (var handle in metadata.AssemblyReferences)
            {
                var reference = metadata.GetAssemblyReference(handle);
                var identity = new System.Reflection.AssemblyName { Name = metadata.GetString(reference.Name), Version = reference.Version,
                    CultureName = reference.Culture.IsNil ? "" : metadata.GetString(reference.Culture) };
                var key = metadata.GetBlobBytes(reference.PublicKeyOrToken);
                if (reference.Flags.HasFlag(System.Reflection.AssemblyFlags.PublicKey)) identity.SetPublicKey(key); else identity.SetPublicKeyToken(key);
                var parents = loaded.Where(assembly => string.Equals(assembly.Location, path, StringComparison.OrdinalIgnoreCase)).ToArray();
                var bindings = parents.Select(parent => System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(parent)!)
                    .Distinct().SelectMany(context => context.Assemblies.Where(candidate => IsCompatibleBinding(identity, candidate.GetName()))).ToArray();
                // Inspect bindings already owned by this CLR context. Forcing every
                // PE reference to load also forces optional, unused graphics backends.
                // An absent unbound reference is left to actual worker execution.
                var matches = bindings.Select(assembly => CaptureBoundAssemblyPath(assembly, assembly.Location))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (matches.Length == 0)
                {
                    var adjacent = Path.Combine(Path.GetDirectoryName(path)!, identity.Name + ".dll");
                    if (File.Exists(adjacent) && System.Reflection.AssemblyName.GetAssemblyName(adjacent).FullName == identity.FullName) matches = [adjacent];
                }
                foreach (var dependency in matches)
                    if (!Path.GetFullPath(dependency).StartsWith(runtime, StringComparison.OrdinalIgnoreCase)) pending.Enqueue(Path.GetFullPath(dependency));
            }
        }
    }

    private static bool IsCompatibleBinding(System.Reflection.AssemblyName reference, System.Reflection.AssemblyName bound)
        => string.Equals(reference.Name, bound.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(reference.CultureName ?? "", bound.CultureName ?? "", StringComparison.OrdinalIgnoreCase)
            && (reference.GetPublicKeyToken() ?? []).SequenceEqual(bound.GetPublicKeyToken() ?? [])
            && (reference.Version == null || bound.Version != null && bound.Version >= reference.Version);

    public static Task<AssetSourceImportResult> Prepare(string[] files, Type? preferredType, CancellationToken cancellation,
        bool isForReImport = false, bool deduplicateMaterials = true, AssetImportAssemblyContext[]? assemblies = null,
        AssetImportOptions? options = null, Action<AssetImportWorkerProgress>? progress = null,
        IReadOnlyDictionary<string, AssetImportOptions>? fileOptions = null)
    {
        cancellation.ThrowIfCancellationRequested();
        var context = assemblies ?? CaptureAssemblies([]);
        ValidateAssemblies(context);
        var output = Path.Combine(Root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(output);
        try
        {
            var request = new AssetImportWorkerParameters { Files = files, PreferredType = preferredType?.AssemblyQualifiedName,
                IsForReImport = isForReImport, DeduplicateMaterials = options?.DeduplicateMaterials ?? deduplicateMaterials,
                AssemblyPaths = context.Select(assembly => assembly.Path).ToArray(), ImporterId = options?.ImporterId?.ToString(),
                Outputs = options?.Outputs?.ToDictionary(pair => pair.Key.AssemblyQualifiedName!, pair => pair.Value), OutputDirectory = output };
            if (fileOptions != null)
                request.FileOptions = fileOptions.ToDictionary(pair => pair.Key, pair => new AssetImportWorkerFileOptions
                { ImporterId = pair.Value.ImporterId?.ToString(), DeduplicateMaterials = pair.Value.DeduplicateMaterials,
                    Outputs = pair.Value.Outputs?.ToDictionary(output => output.Key.AssemblyQualifiedName!, output => output.Value) }, StringComparer.OrdinalIgnoreCase);
            // Each job loads fixed copies of its real extension/project modules.
            // The origin files remain guarded independently until result acceptance.
            var copiedModules = new Dictionary<string, string>(); var copiedPaths = new List<string>();
            foreach (var assembly in context)
            {
                cancellation.ThrowIfCancellationRequested();
                var directory = Path.Combine(output, "assemblies", copiedPaths.Count.ToString()); Directory.CreateDirectory(directory);
                var copy = Path.Combine(directory, Path.GetFileName(assembly.Path));
                using (var source = new FileStream(assembly.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var target = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None)) source.CopyTo(target);
                if (Hash(copy) != assembly.Hash) throw new IOException("An importer assembly changed while its context was captured: " + assembly.Path);
                copiedPaths.Add(copy); copiedModules.Add(copy, assembly.ModuleVersion);
            }
            request.AssemblyPaths = copiedPaths.ToArray(); request.AssemblyModules = copiedModules;
            File.WriteAllText(Path.Combine(output, "assembly-context.json"), JsonSerializer.Serialize(context.Select((assembly, index) => new
            {
                Origin = assembly.Path, Copy = copiedPaths[index], Identity = System.Reflection.AssemblyName.GetAssemblyName(assembly.Path).FullName,
                assembly.Hash, assembly.ModuleVersion
            }), new JsonSerializerOptions { WriteIndented = true }));
            var requestPath = Path.Combine(output, "request.json"); File.WriteAllText(requestPath, JsonSerializer.Serialize(request));
            CompiledProjectBuild.Run(AssetCompilerLocator.GetExecutable(), ["import-source", requestPath], output, "import", cancellation, state =>
            {
                if (cancellation.IsCancellationRequested || state.OutputLine is not { } line || !line.StartsWith(AssetImportWorkerProgress.Prefix, StringComparison.Ordinal)) return;
                var update = JsonSerializer.Deserialize<AssetImportWorkerProgress>(line[AssetImportWorkerProgress.Prefix.Length..]);
                if (update != null) progress?.Invoke(update);
            });
            cancellation.ThrowIfCancellationRequested(); ValidateAssemblies(context);
            var result = JsonSerializer.Deserialize<AssetImportWorkerResult>(File.ReadAllText(Path.Combine(output, "result.json")))
                ?? throw new InvalidDataException("The native importer produced no result.");
            var log = new LoggerResult("Asset import");
            foreach (var message in result.Messages)
            {
                if (!Enum.TryParse<LogMessageType>(message.Level, out var level)) throw new InvalidDataException("Invalid native importer message level.");
                log.Log(new LogMessage("Asset import", level, message.Text));
            }
            var imported = new List<AssetItem>();
            var assetSources = new Dictionary<AssetId, string>();
            foreach (var asset in result.Assets)
            {
                cancellation.ThrowIfCancellationRequested();
                if (Path.GetFileName(asset.Snapshot) != asset.Snapshot) throw new InvalidDataException("The native importer snapshot is outside its owned directory.");
                var loaded = AssetFileSerializer.Load<Asset>(Path.Combine(output, asset.Snapshot), log);
                if (loaded.Asset.Id != new AssetId(Guid.Parse(asset.Id)) || loaded.Asset.GetType().AssemblyQualifiedName != asset.Type
                    || loaded.Asset is SourceCodeAsset or IProjectAsset) throw new InvalidDataException("The native importer asset identity or type changed.");
                SourceHashesHelper.ConvertPaths(loaded.Asset, new UDirectory(output), UPathType.Absolute);
                var item = new AssetItem(new UFile(asset.Url), loaded.Asset); item.YamlMetadata.ReplaceWith(loaded.YamlMetadata);
                imported.Add(item);
                var origin = Path.GetFullPath(asset.SourceFile);
                if (!files.Contains(origin, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("The importer asset has no requested source provenance.");
                assetSources.Add(loaded.Asset.Id, origin);
            }
            if (log.HasErrors) throw new InvalidDataException(log.ToText());
            var content = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
            foreach (var hash in result.ContentHashes)
            {
                if (!ObjectId.TryParse(hash.Value, out var id)) throw new InvalidDataException("The native importer source hash is invalid.");
                content.Add(hash.Key, id);
            }
            var hashes = new Dictionary<string, string>(result.SourceHashes, StringComparer.OrdinalIgnoreCase);
            ValidateSources(hashes);
            return Task.FromResult(new AssetSourceImportResult(imported.ToArray(), hashes, log, content, assetSources));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            var log = Path.Combine(output, "import.log");
            var archive = Path.Combine(Root, "logs"); Directory.CreateDirectory(archive);
            var archivedPath = Path.Combine(archive, Path.GetFileName(output) + ".log");
            if (File.Exists(log)) File.Copy(log, archivedPath, false);
            var contextManifest = Path.Combine(output, "assembly-context.json");
            if (File.Exists(contextManifest)) File.Copy(contextManifest, Path.Combine(archive, Path.GetFileName(output) + ".assemblies.json"), false);
            throw new InvalidOperationException("Asset import failed. " + error.Message + (File.Exists(archivedPath) ? "\nImport log: " + archivedPath : ""), error);
        }
        finally
        {
            var resolved = Path.GetFullPath(output);
            if (!string.Equals(Path.GetDirectoryName(resolved), Root, StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(Path.GetFileName(resolved), "N", out _))
                throw new InvalidOperationException("Native import output is outside the owned cache directory.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }
    private static void ValidateAssemblies(IEnumerable<AssetImportAssemblyContext> assemblies)
    {
        foreach (var assembly in assemblies) if (Hash(assembly.Path) != assembly.Hash) throw new IOException("An importer assembly changed during import: " + assembly.Path);
    }
    public static AssetSourceImportResult CaptureSources(string[] files, CancellationToken cancellation)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var content = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        { cancellation.ThrowIfCancellationRequested(); var fingerprint = Fingerprint(file, cancellation); hashes[file] = fingerprint.Sha; content[file] = fingerprint.Object; }
        return new([], hashes, new LoggerResult("Asset import"), content);
    }
    private static (string Sha, ObjectId Object) Fingerprint(string path, CancellationToken cancellation)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Fingerprint(stream, cancellation);
    }
    internal static (string Sha, ObjectId Object) Fingerprint(Stream stream, CancellationToken cancellation)
    {
        if (stream.CanSeek) stream.Position = 0;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var builder = new ObjectIdBuilder(); var buffer = new byte[65536]; int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
        { cancellation.ThrowIfCancellationRequested(); sha.AppendData(buffer, 0, count); builder.Write(buffer, 0, count); }
        return (Convert.ToHexString(sha.GetHashAndReset()), builder.ComputeHash());
    }
    public static void ValidateSources(IReadOnlyDictionary<string, string> hashes)
    {
        foreach (var source in hashes) if (source.Value != Hash(source.Key)) throw new IOException("The source file changed during import: " + source.Key);
    }
    private static string Hash(string path)
    { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); return Convert.ToHexString(SHA256.HashData(stream)); }
}
