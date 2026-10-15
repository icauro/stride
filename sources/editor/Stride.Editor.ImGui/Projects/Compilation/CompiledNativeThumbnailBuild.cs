using System.Text.Json;
using Stride.Editor.Thumbnails;
using Stride.Assets.Media;
using System.Security.Cryptography;

namespace Stride.Editor.ImGui;

internal static class CompiledNativeThumbnailBuild
{
    private static readonly SemaphoreSlim WorkerGate = new(1, 1);
    private static string Root => Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Stride", "Editor.ImGui", "native-thumbnails"));

    internal static byte[] Build(NativeThumbnailCompilationSnapshot snapshot, CancellationToken token, Action<ProjectLoadState> progress)
    {
        using var artifactLease = snapshot.AcquireArtifactLease();
        WorkerGate.Wait(token);
        try { return BuildCore(snapshot, token, progress); }
        finally { WorkerGate.Release(); }
    }
    private static byte[] BuildCore(NativeThumbnailCompilationSnapshot snapshot, CancellationToken token, Action<ProjectLoadState> progress)
    {
        token.ThrowIfCancellationRequested();
        var output = Path.Combine(Root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(output);
        try
        {
            var inputs = JsonSerializer.Deserialize<MediaPreviewBuildParameters>(JsonSerializer.Serialize(snapshot.Authored.Parameters))!;
            inputs.OutputDirectory = output; inputs.BuildDirectory = Path.Combine(Root, "cache", inputs.RootId, "objects");
            var assets = new List<MediaPreviewAssetInput>();
            foreach (var asset in snapshot.Authored.Assets)
            {
                token.ThrowIfCancellationRequested();
                var input = new MediaPreviewAssetInput { Id = asset.Input.Id, Url = asset.Input.Url,
                    AssetPath = asset.Input.AssetPath, LocalUrl = asset.Input.LocalUrl, SourcePackagePath = asset.Input.SourcePackagePath, PackageDependencies = asset.Input.PackageDependencies.ToArray(), AssetNamespace = asset.Input.AssetNamespace,
                    Snapshot = Path.Combine(output, asset.Input.Id + asset.Extension) };
                File.WriteAllBytes(input.Snapshot, asset.Bytes); assets.Add(input);
            }
            inputs.Assets = assets.ToArray();
            var assemblies = new List<NativeThumbnailAssemblyInput>();
            foreach (var assembly in snapshot.Assemblies)
            {
                token.ThrowIfCancellationRequested();
                var folder = Path.Combine(output, "assemblies", assembly.Certificate.ModuleVersionId); Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, Path.GetFileName(assembly.Certificate.Path)); assembly.Artifact.CopyTo(path, assembly.Certificate);
                assemblies.Add(new NativeThumbnailAssemblyInput { Path = path, Identity = assembly.Certificate.Identity,
                    ModuleVersionId = assembly.Certificate.ModuleVersionId, Sha256 = assembly.Certificate.Sha256 });
            }
            inputs.AssemblyPaths = assemblies.Select(assembly => assembly.Path).ToArray();
            var image = Path.Combine(output, "thumbnail.png");
            var request = new NativeThumbnailBuildParameters { Inputs = inputs, Assemblies = assemblies.ToArray(),
                CompilerType = snapshot.CompilerType, OutputPng = image,
                SourceHashes = new Dictionary<string, string>(snapshot.SourceHashes, StringComparer.OrdinalIgnoreCase),
                AssetHashes = snapshot.Authored.Assets.ToDictionary(asset => asset.Input.Id, asset => Convert.ToHexString(SHA256.HashData(asset.Bytes))) };
            var requestPath = Path.Combine(output, "request.json"); File.WriteAllText(requestPath, JsonSerializer.Serialize(request));
            var worker = NativeThumbnailWorkerDeployment.Create(Path.Combine(output, "worker"), snapshot, token);
            CompiledProjectBuild.Run(worker, ["preview-thumbnail", requestPath], output,
                "thumbnail", token, progress, "thumbnail:" + inputs.RootId);
            token.ThrowIfCancellationRequested();
            if (!File.Exists(image) || new FileInfo(image).Length > 1024 * 1024)
                throw new InvalidDataException("The native thumbnail worker produced no valid bounded PNG.");
            return File.ReadAllBytes(image);
        }
        finally
        {
            var absolute = Path.GetFullPath(output);
            if (string.Equals(Path.GetDirectoryName(absolute), Root, StringComparison.OrdinalIgnoreCase)
                && Guid.TryParseExact(Path.GetFileName(absolute), "N", out _) && Directory.Exists(absolute)) Directory.Delete(absolute, true);
        }
    }
}
