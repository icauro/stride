using Stride.Audio.Assets;
using Stride.Core.Assets;
using Stride.Core.IO;

namespace Stride.Editor.ImGui;

internal sealed record AudioParityFixture(string AssetId, string AssetPath, string SourcePath);

internal sealed partial class EditorGame
{
    internal AudioParityFixture PrepareAudioParityFixture()
    {
        if (!args.Contains("--ui-test") || workspace != null || projectLoading != null)
            throw new InvalidOperationException("Prepare audio parity before loading an explicitly private UI-test project.");
        var packagePath = Path.GetFullPath(projectPathInput);
        var privateRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Stride-ImGui-UI")) + Path.DirectorySeparatorChar;
        if (!packagePath.StartsWith(privateRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Audio fixture must belong to the disposable UI copy.");
        var root = Path.GetDirectoryName(packagePath)!;
        var token = "AudioParity-" + Guid.NewGuid().ToString("N");
        var source = Path.Combine(root, "Resources", token + ".wav");
        var path = Path.Combine(root, "Assets", "Parity", token + SoundAsset.FileExtension);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Actual one-second mono PCM source; silence keeps setup/load harmless even
        // before the real instance's explicitly zero-volume playback contract.
        const int sampleRate = 44100, samples = sampleRate, bytes = samples * 2;
        using (var stream = new FileStream(source, FileMode.CreateNew, FileAccess.Write))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + bytes);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(sampleRate); writer.Write(sampleRate * 2);
            writer.Write((short)2); writer.Write((short)16);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(bytes); writer.Write(new byte[bytes]);
        }
        var asset = new SoundAsset { Source = new UFile(Path.GetRelativePath(Path.GetDirectoryName(path)!, source)),
            SampleRate = sampleRate, StreamFromDisk = false, Spatialized = false };
        AssetFileSerializer.Save(path, asset, null);
        var package = AssetFileSerializer.Load<Package>(packagePath).Asset;
        var packageOwner = new StandalonePackage(package);
        package.RootAssets.Add(new AssetReference(asset.Id, packageOwner.Qualify(new UFile("Parity/" + token))));
        AssetFileSerializer.Save(packagePath, package, null);
        return new(asset.Id.ToString(), path, source);
    }

    internal string ResolveAudioParityRuntimeUrl(AudioParityFixture fixture)
    {
        if (!args.Contains("--ui-test") || workspace == null || projectContent == null)
            throw new InvalidOperationException("Resolve audio parity only after the real disposable project compiles.");
        var item = workspace.AssetItems.Single(item => item.Asset.Id.ToString() == fixture.AssetId);
        if (item.Asset is not SoundAsset || !string.Equals(Path.GetFullPath(item.FullPath.ToOSPath()),
            Path.GetFullPath(fixture.AssetPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Audio parity authored owner changed during project loading.");
        var url = item.Location.ToString();
        if (!Content.FileProvider.ObjectDatabase.ContentIndexMap.Contains(url))
        {
            var artifactRoot = Environment.GetEnvironmentVariable("STRIDE_EDITOR_TEST_ARTIFACTS")
                ?? throw new InvalidOperationException("Audio verification requires its owned diagnostic artifact directory.");
            var token = Path.GetFileNameWithoutExtension(fixture.AssetPath);
            var output = projectContent.OutputPath;
            var logs = new Dictionary<string, string>();
            foreach (var name in new[] { "project.log", "assets.log" })
            {
                var log = Path.Combine(output, name);
                if (File.Exists(log)) logs.Add(name, File.ReadAllText(log));
            }
            File.WriteAllText(Path.Combine(artifactRoot, "audio-runtime-url-missing.json"), System.Text.Json.JsonSerializer.Serialize(new
            {
                fixture, expectedUrl = url, localUrl = item.UnqualifiedUrl.ToString(),
                packageNamespace = item.Package?.AssetNamespace, containerNamespace = item.Package?.Container?.AssetNamespace,
                packagePath = item.Package?.FullPath?.ToString(), packageRoot = item.Package?.RootDirectory?.ToString(),
                compiledOutput = output, workspaceOutput = workspace.CompiledOutput, workspaceFailure = workspace.CompilationFailure?.ToString(),
                indexIdentity = Content.FileProvider.ObjectDatabase.GetHashCode(),
                matchingNativeIndexEntries = Content.FileProvider.ObjectDatabase.ContentIndexMap.SearchValues(entry => entry.Key.Contains(token, StringComparison.Ordinal)).ToArray(),
                nativePackages = workspace.Session.Packages.Select(package => new
                {
                    package.Meta.Name, package.Kind, path = package.FullPath?.ToString(),
                    declaredHostAssemblies = package.HostAssemblies.Select(assembly => new { assembly.TargetFramework, path = assembly.Path?.ToString() }).ToArray(),
                    nativeContainerAssemblies = (package.Container as StandalonePackage)?.Assemblies.ToArray(),
                    assemblies = package.LoadedAssemblies.Select(assembly => assembly.Assembly?.FullName).ToArray(),
                }).ToArray(),
                nativeLoadDiagnostics = workspace.NativeLoadDiagnostics.Select(message => message.ToString()).ToArray(), logs,
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            throw new InvalidOperationException("The real sound fixture was not compiled into its owning package URL.");
        }
        return url;
    }
}
