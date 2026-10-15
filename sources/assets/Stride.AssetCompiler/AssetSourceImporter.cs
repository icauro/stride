using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Stride.Assets.Models;
using Stride.Core.Assets;
using Stride.Core.Assets.Tracking;
using Stride.Core.Diagnostics;
using Stride.Core.IO;
using Stride.Core.Reflection;
using Stride.Core.Storage;

namespace Stride.AssetCompiler;

internal static class AssetSourceImporter
{
    public static int Run(string requestPath)
    {
        var request = JsonSerializer.Deserialize<AssetImportWorkerParameters>(File.ReadAllText(requestPath))
            ?? throw new InvalidDataException("Invalid native importer request.");
        var assemblyPaths = request.AssemblyPaths.GroupBy(path => AssemblyName.GetAssemblyName(path).FullName!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group =>
            {
                if (group.Select(path => request.AssemblyModules[path]).Distinct(StringComparer.Ordinal).Count() != 1)
                    throw new InvalidDataException("The importer context contains conflicting modules for assembly " + group.Key);
                return group.First();
            }, StringComparer.Ordinal);
        ResolveEventHandler resolve = (_, args) => assemblyPaths.TryGetValue(args.Name, out var resolved) ? Assembly.LoadFrom(resolved) : null;
        AppDomain.CurrentDomain.AssemblyResolve += resolve;
        foreach (var path in request.AssemblyPaths)
        {
            var identity = AssemblyName.GetAssemblyName(path);
            var sameName = AppDomain.CurrentDomain.GetAssemblies().Where(candidate => candidate.GetName().Name == identity.Name).ToArray();
            Console.WriteLine("Importer assembly binding: " + JsonSerializer.Serialize(new
            {
                RequestedIdentity = identity.FullName, RequestedPath = path, RequestedModule = request.AssemblyModules.GetValueOrDefault(path),
                Bound = sameName.Select(candidate => new { Identity = candidate.FullName, Path = candidate.IsDynamic ? null : candidate.Location,
                    Module = candidate.IsDynamic ? (Guid?)null : candidate.ManifestModule.ModuleVersionId,
                    Context = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(candidate)?.Name })
            }));
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(candidate => candidate.FullName == identity.FullName) ?? Assembly.LoadFrom(path);
            if (!request.AssemblyModules.TryGetValue(path, out var expectedModule) || assembly.ManifestModule.ModuleVersionId != Guid.Parse(expectedModule))
                throw new InvalidDataException("The importer worker assembly context differs from the loaded editor module: " + identity.FullName);
            RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);
            AssemblyRegistry.Register(assembly, AssemblyCommonCategories.Assets);
        }
        var preferred = request.PreferredType == null ? null : Type.GetType(request.PreferredType, true);
        var logger = new LoggerResult("Asset import"); logger.MessageLogged += (_, args) => Console.WriteLine(args.Message);
        var output = new AssetImportWorkerResult(); var assets = new List<AssetImportWorkerAsset>();
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var contentHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Directory.CreateDirectory(request.OutputDirectory);
        for (var fileIndex = 0; fileIndex < request.Files.Length; fileIndex++)
        {
            var file = Path.GetFullPath(request.Files[fileIndex]); hashes[file] = Fingerprint(file).Sha;
            var fileOptions = request.FileOptions.FirstOrDefault(pair => string.Equals(Path.GetFullPath(pair.Key), file, StringComparison.OrdinalIgnoreCase)).Value;
            var importerId = fileOptions?.ImporterId ?? request.ImporterId;
            var selectedOutputs = fileOptions?.Outputs ?? request.Outputs;
            var importer = importerId == null
                ? AssetRegistry.FindImporterForFile(file).FirstOrDefault(candidate => preferred == null || candidate.RootAssetTypes.Contains(preferred))
                : AssetRegistry.FindImporterById(Guid.Parse(importerId));
            if (importer == null || !importer.IsSupportingFile(file)) throw new InvalidOperationException("No importer found for file \"" + file + "\".");
            if (preferred != null && !importer.RootAssetTypes.Contains(preferred)) throw new InvalidOperationException("The selected importer does not support the requested asset type.");
            var parameters = importer.GetDefaultParameters(request.IsForReImport); parameters.Logger = logger;
            if (preferred != null && (request.IsForReImport || preferred != typeof(ModelAsset)))
                foreach (var type in parameters.SelectedOutputTypes.Keys.ToArray()) parameters.SelectedOutputTypes[type] = type == preferred;
            if (selectedOutputs != null)
                foreach (var option in selectedOutputs)
                {
                    var type = Type.GetType(option.Key, true)!;
                    if (!parameters.SelectedOutputTypes.ContainsKey(type)) throw new InvalidDataException("The importer does not support the selected output type.");
                    parameters.SelectedOutputTypes[type] = option.Value;
                }
            if (!parameters.HasSelectedOutputTypes) throw new InvalidOperationException("No asset output type is selected.");
            if (importer is ModelAssetImporter) parameters.InputParameters.Set(ModelAssetImporter.DeduplicateMaterialsKey, fileOptions?.DeduplicateMaterials ?? request.DeduplicateMaterials);
            void Progress(string text, bool known = false, int step = 0, int count = 0, bool completed = false,
                AssetItem? asset = null, int assetIndex = 0, int assetCount = 0)
                => Console.WriteLine(AssetImportWorkerProgress.Prefix + JsonSerializer.Serialize(new AssetImportWorkerProgress
                { File = file, Importer = importer.Name, FileIndex = fileIndex, FileCount = request.Files.Length, Message = text,
                  HasKnownSteps = known, CurrentStep = step, StepCount = count, Completed = completed,
                  AssetUrl = asset?.Location.ToString(), AssetType = asset?.Asset.GetType().AssemblyQualifiedName,
                  AssetIndex = assetIndex, AssetCount = assetCount }));
            EventHandler<ProgressStatusEventArgs> onProgress = (_, args) => Progress(args.Message, args.HasKnownSteps, args.CurrentStep, args.StepCount);
            logger.ProgressChanged += onProgress;
            AssetItem[] imported;
            try { Progress("Importing"); imported = importer.Import(new UFile(file), parameters).ToArray(); }
            finally { logger.ProgressChanged -= onProgress; }
            if (logger.HasErrors) throw new InvalidDataException(logger.ToText());
            if (imported.Length == 0) throw new InvalidDataException("The importer produced no assets for \"" + file + "\".");
            if (hashes[file] != Fingerprint(file).Sha) throw new IOException("The source file changed during import: " + file);
            var emittedAssets = 0;
            foreach (var item in imported)
            {
                if (item.Package != null || item.Asset is SourceCodeAsset or IProjectAsset) throw new InvalidDataException("The importer returned an unsupported authored asset.");
                var collector = new SourceFilesCollector(); var required = collector.GetCompilationInputFiles(item.Asset);
                var assetHashes = new Dictionary<UFile, ObjectId>();
                foreach (var source in collector.GetSourceFiles(item.Asset).Keys.Union(required))
                {
                    var path = Path.GetFullPath(source.IsAbsolute ? source.ToOSPath() : Path.Combine(Path.GetDirectoryName(file)!, source.ToOSPath()));
                    if (!File.Exists(path)) { if (required.Contains(source)) throw new FileNotFoundException("An imported source file is missing.", path); continue; }
                    var fingerprint = Fingerprint(path);
                    if (hashes.TryGetValue(path, out var baseline) && baseline != fingerprint.Sha) throw new IOException("The source file changed during import: " + path);
                    hashes[path] = fingerprint.Sha; contentHashes[path] = fingerprint.Object.ToString(); assetHashes[new UFile(path)] = fingerprint.Object;
                }
                foreach (var member in collector.GetSourceMembers(item.Asset))
                {
                    if (member.Value == null || member.Value.IsAbsolute || string.IsNullOrWhiteSpace(member.Value.ToString())) continue;
                    if (!member.Key.Apply(item.Asset, MemberPathAction.ValueSet, new UFile(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, member.Value.ToOSPath())))))
                        throw new InvalidOperationException("An imported source member could not be rebound.");
                }
                SourceHashesHelper.UpdateHashes(item.Asset, assetHashes);
                // The serializer consumes the actual generated asset and native YAML
                // metadata. Absolute source keys preserve origin across the worker boundary.
                SourceHashesHelper.ConvertPaths(item.Asset, new UDirectory(Path.GetDirectoryName(file)!), UPathType.Relative);
                SourceHashesHelper.ConvertPaths(item.Asset, new UDirectory(Path.GetDirectoryName(file)!), UPathType.Absolute);
                var extension = AssetRegistry.GetDefaultExtension(item.Asset.GetType());
                if (string.IsNullOrEmpty(extension)) throw new InvalidDataException("The imported asset has no registered serializer extension.");
                var snapshot = item.Id + extension; var pathToSnapshot = Path.Combine(request.OutputDirectory, snapshot);
                using (var stream = new FileStream(pathToSnapshot, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    AssetFileSerializer.Save(stream, item.Asset, item.YamlMetadata);
                assets.Add(new() { Id = item.Id.ToString(), Url = item.Location.ToString(), Snapshot = snapshot, Type = item.Asset.GetType().AssemblyQualifiedName!, SourceFile = file });
                Progress("Imported", asset: item, assetIndex: emittedAssets++, assetCount: imported.Length);
            }
            Progress("Imported", completed: true);
        }
        foreach (var source in hashes) if (Fingerprint(source.Key).Sha != source.Value) throw new IOException("The source file changed during import: " + source.Key);
        output.Assets = assets.ToArray(); output.SourceHashes = hashes; output.ContentHashes = contentHashes;
        output.Messages = logger.Messages.Select(message => new AssetImportWorkerMessage { Level = message.Type.ToString(), Text = message.Text }).ToArray();
        File.WriteAllText(Path.Combine(request.OutputDirectory, "result.json"), JsonSerializer.Serialize(output));
        return 0;
    }
    private static (string Sha, ObjectId Object) Fingerprint(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var builder = new ObjectIdBuilder(); var buffer = new byte[65536]; int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) != 0) { sha.AppendData(buffer, 0, count); builder.Write(buffer, 0, count); }
        return (Convert.ToHexString(sha.GetHashAndReset()), builder.ComputeHash());
    }
}
