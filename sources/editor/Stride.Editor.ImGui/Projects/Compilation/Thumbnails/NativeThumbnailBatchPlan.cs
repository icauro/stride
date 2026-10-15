using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Stride.Editor.Thumbnails;

namespace Stride.Editor.ImGui;

// Canonical capture merger for exact-generation resident builds.
internal sealed record NativeThumbnailBatchPlan(string Generation,
    NativeThumbnailCompilationSnapshot[] Requests, MediaAssetSnapshot[] Assets,
    NativeThumbnailAssemblySnapshot[] Assemblies, IReadOnlyDictionary<string, string> SourceHashes)
{
    internal static string GenerationOf(NativeThumbnailCompilationSnapshot request)
    {
        var input = request.Authored.Parameters;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Stamp(input.GraphicsPlatform + "|" + input.Platform + "|" + input.GraphicsProfile + "|" + input.ColorSpace + "|" + input.TextureQuality + "|" + request.Authored.Hdr);
        foreach (var module in request.Assemblies.OrderBy(item => item.Certificate.Identity, StringComparer.Ordinal))
            Stamp(module.Certificate.Identity + "|" + module.Certificate.ModuleVersionId + "|" + module.Certificate.Sha256);
        foreach (var source in (request.ShaderSources ?? []).OrderBy(item => item.RelativePath, StringComparer.Ordinal))
            Stamp(source.RelativePath + "|" + source.Sha256);
        foreach (var asset in request.Authored.Assets.Where(item => item.Input.Id == input.GameSettingsId
                     || item.Extension is ".sdsl" or ".sdfx").OrderBy(item => item.Input.Id, StringComparer.Ordinal))
        {
            Stamp(asset.Input.Id + "|" + asset.Input.Url + "|" + asset.Input.AssetPath);
            hash.AppendData(asset.Bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
        void Stamp(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            hash.AppendData(BitConverter.GetBytes(bytes.Length)); hash.AppendData(bytes);
        }
    }

    internal static NativeThumbnailBatchPlan Create(IReadOnlyList<NativeThumbnailCompilationSnapshot> requests)
    {
        if (requests.Count == 0) throw new ArgumentException("A native thumbnail batch requires actual captured roots.");
        var generation = GenerationOf(requests[0]);
        var assets = new Dictionary<string, MediaAssetSnapshot>(StringComparer.Ordinal);
        var urls = new Dictionary<string, string>(StringComparer.Ordinal);
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var roots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var request in requests)
        {
            if (GenerationOf(request) != generation)
                throw new InvalidDataException("Native thumbnail roots belong to different certified worker generations.");
            if (!roots.Add(request.Authored.Parameters.RootId))
                throw new InvalidDataException("A native thumbnail batch contains multiple revisions of one root.");
            foreach (var asset in request.Authored.Assets)
            {
                if (assets.TryGetValue(asset.Input.Id, out var existing)
                    && (existing.Extension != asset.Extension || existing.Input.Url != asset.Input.Url
                        || existing.Input.AssetPath != asset.Input.AssetPath || existing.Input.AssetNamespace != asset.Input.AssetNamespace
                        || existing.Input.LocalUrl != asset.Input.LocalUrl || existing.Input.SourcePackagePath != asset.Input.SourcePackagePath
                        || !existing.Input.PackageDependencies.SequenceEqual(asset.Input.PackageDependencies)
                        || !existing.Bytes.AsSpan().SequenceEqual(asset.Bytes)))
                    throw new InvalidDataException("Native thumbnail roots contain conflicting canonical asset snapshots: " + asset.Input.Id);
                if (urls.TryGetValue(asset.Input.Url, out var id) && id != asset.Input.Id)
                    throw new InvalidDataException("Native thumbnail roots contain a conflicting runtime asset URL: " + asset.Input.Url);
                assets[asset.Input.Id] = asset; urls[asset.Input.Url] = asset.Input.Id;
            }
            foreach (var source in request.SourceHashes)
            {
                if (sources.TryGetValue(source.Key, out var previous) && previous != source.Value)
                    throw new InvalidDataException("Native thumbnail roots contain conflicting physical source certificates: " + source.Key);
                sources[source.Key] = source.Value;
            }
        }
        return new(generation, requests.ToArray(), assets.Values.ToArray(), requests[0].Assemblies, sources);
    }

    internal NativeThumbnailBatchParameters Stage(string directory, string buildDirectory, CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        var input = JsonSerializer.Deserialize<Stride.Assets.Media.MediaPreviewBuildParameters>(JsonSerializer.Serialize(Requests[0].Authored.Parameters))!;
        input.OutputDirectory = directory; input.BuildDirectory = buildDirectory;
        var inputs = new List<Stride.Assets.Media.MediaPreviewAssetInput>();
        foreach (var asset in Assets)
        {
            token.ThrowIfCancellationRequested();
            var staged = new Stride.Assets.Media.MediaPreviewAssetInput { Id = asset.Input.Id, Url = asset.Input.Url,
                AssetPath = asset.Input.AssetPath, LocalUrl = asset.Input.LocalUrl, SourcePackagePath = asset.Input.SourcePackagePath, PackageDependencies = asset.Input.PackageDependencies.ToArray(), AssetNamespace = asset.Input.AssetNamespace,
                Snapshot = Path.Combine(directory, asset.Input.Id + asset.Extension) };
            File.WriteAllBytes(staged.Snapshot, asset.Bytes); inputs.Add(staged);
        }
        input.Assets = inputs.ToArray();
        // Generation deployment owns the certified immutable DLLs. Request paths
        // are rebound by the actual worker host after acquiring its generation lease.
        return new NativeThumbnailBatchParameters { Generation = Generation, Inputs = input,
            Assemblies = Assemblies.Select(item => item.Certificate).ToArray(),
            AssetHashes = Assets.ToDictionary(item => item.Input.Id, item => Convert.ToHexString(SHA256.HashData(item.Bytes))),
            SourceHashes = new Dictionary<string, string>(SourceHashes, StringComparer.OrdinalIgnoreCase),
            Roots = Requests.Select(item => new NativeThumbnailBatchRoot { AssetId = item.Authored.Parameters.RootId,
                Identity = item.Identity, CompilerType = item.CompilerType }).ToArray() };
    }
}
