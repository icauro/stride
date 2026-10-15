using System.Text;
using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using Stride.Core.Assets;
using Stride.Core.Assets.Tracking;
using Stride.Core.IO;

namespace Stride.Editor.ImGui;

/// <summary>Derived input cache only. Actual compiler boundaries still capture and validate full source bytes.</summary>
internal sealed class AuthoredImportPollingCache
{
    internal sealed record Entry(string Inputs, AssetId[] Roots, MediaCompilationSnapshot Snapshot, string[] Sources);
    private readonly Dictionary<AssetId, Entry> entries = [];
    private static readonly ConditionalWeakTable<AssetItem, AssetPathCertificate> assetPaths = new();
    private readonly record struct AssetPathInputs(Package? Package, string? PackagePath, string SourceFolder,
        string Location, string? AssetNamespace, string? AlternativePath, Type AssetType, string? Extension);
    private sealed class AssetPathCertificate
    {
        public AssetPathInputs Inputs;
        public (string Path, string OsPath) Value;
        public bool Initialized;
    }
    internal static (string Path, string OsPath) CaptureAssetPath(AssetItem item)
    {
        var package = item.Package;
        var type = item.Asset.GetType();
        var inputs = new AssetPathInputs(package, package?.FullPath?.ToString(),
            (item.SourceFolder ?? (package != null ? package.GetDefaultAssetFolder() : UDirectory.This)).ToString(),
            item.Location.ToString(), package?.Container?.AssetNamespace ?? package?.AssetNamespace,
            item.AlternativePath?.ToString(), type, item.AlternativePath == null ? AssetRegistry.GetDefaultExtension(type) : null);
        var cached = assetPaths.GetValue(item, _ => new());
        if (!cached.Initialized || cached.Inputs != inputs)
        {
            // The native getter remains authoritative. The compared inputs are its
            // complete path dependencies, including mutable package/registry inputs.
            var fullPath = item.FullPath;
            cached.Value = (fullPath.ToString(), fullPath.ToOSPath());
            cached.Inputs = inputs; cached.Initialized = true;
        }
        return cached.Value;
    }
    public void Clear() => entries.Clear();

    public static string CaptureInputs(ProjectWorkspace workspace, EditorAuthoringRegistry? registry)
    {
        using var result = new InputCertificate();
        foreach (var package in workspace.Session.Packages.OrderBy(package => package.FullPath?.ToString(), StringComparer.Ordinal))
        {
            result.Append(package.FullPath?.ToString());
            foreach (var assembly in package.LoadedAssemblies.Where(assembly => assembly.Assembly != null && !string.IsNullOrEmpty(assembly.Path))
                .OrderBy(assembly => assembly.Path.ToString(), StringComparer.OrdinalIgnoreCase))
                AppendFile(result, Path.GetFullPath(assembly.Path));
            foreach (var item in package.Assets.OrderBy(item => item.Id.ToString(), StringComparer.Ordinal))
            {
                var state = registry?.Find(item.Id);
                var fullPath = CaptureAssetPath(item);
                result.Append(item.Id.ToString()); result.Append(item.Location.ToString()); result.Append(fullPath.Path);
                result.Append(item.Asset.GetType().AssemblyQualifiedName);
                result.Append(state?.Generation.ToString("D"));
                result.Append((state?.Document as IAssetTagDocument)?.EditRevision.ToString(CultureInfo.InvariantCulture));
                AppendFile(result, fullPath.OsPath);
            }
        }
        return result.Finish();
    }
    private static void AppendFile(InputCertificate result, string path)
    {
        var file = new FileInfo(path);
        result.Append(path); result.Append(file.Exists ? "1" : "0");
        if (file.Exists)
        {
            result.Append(file.Length.ToString(CultureInfo.InvariantCulture));
            result.Append(file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
            result.Append(file.CreationTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
        }
    }
    private static string WithSources(string inputs, IReadOnlyList<string> sources)
    {
        using var result = new InputCertificate();
        result.Append(inputs);
        foreach (var source in sources) AppendFile(result, source);
        return result.Finish();
    }
    // These are derived polling certificates, never authoring or publication snapshots.
    // Length framing preserves field boundaries without retaining/copying the full session
    // text for every root. Actual compiler boundaries still validate full source bytes.
    internal static string ComputeCertificate(params string?[] values)
    {
        using var certificate = new InputCertificate();
        foreach (var value in values) certificate.Append(value);
        return certificate.Finish();
    }
    private sealed class InputCertificate : IDisposable
    {
        private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly byte[] buffer = ArrayPool<byte>.Shared.Rent(16384);
        private int buffered;
        public void Append(string? value)
        {
            var count = value == null ? -1 : Encoding.UTF8.GetByteCount(value);
            if (buffer.Length - buffered < 4) Flush();
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(buffered, 4), count);
            buffered += 4;
            if (count <= 0) return;
            if (count <= buffer.Length)
            {
                if (buffer.Length - buffered < count) Flush();
                buffered += Encoding.UTF8.GetBytes(value!, buffer.AsSpan(buffered));
                return;
            }
            Flush();
            var rented = ArrayPool<byte>.Shared.Rent(count);
            try
            {
                var written = Encoding.UTF8.GetBytes(value!, rented); hash.AppendData(rented.AsSpan(0, written));
            }
            finally { ArrayPool<byte>.Shared.Return(rented); }
        }
        private void Flush()
        {
            if (buffered == 0) return;
            hash.AppendData(buffer.AsSpan(0, buffered)); buffered = 0;
        }
        public string Finish()
        {
            Flush(); return Convert.ToHexString(hash.GetHashAndReset());
        }
        public void Dispose()
        {
            hash.Dispose(); ArrayPool<byte>.Shared.Return(buffer);
        }
    }
    public Entry Capture(ProjectWorkspace workspace, EditorAuthoringRegistry? registry, AssetEntry root, string inputs,
        Func<AssetId[], MediaCompilationSnapshot> capture, bool includeInboundDependencies = true)
    {
        if (entries.TryGetValue(root.Id, out var existing) && WithSources(inputs, existing.Sources) == existing.Inputs) return existing;
        var roots = includeInboundDependencies ? AuthoredImportDependencyClosure.Find(workspace, registry, root.Id) : [root.Id];
        var snapshot = capture(roots);
        var sources = new HashSet<string>(snapshot.Parameters.AssemblyPaths, StringComparer.OrdinalIgnoreCase);
        foreach (var input in snapshot.Assets)
        {
            var item = workspace.Session.FindAsset(new AssetId(input.Input.Id));
            if (item == null) continue;
            var asset = registry?.Find(item.Id)?.Document is IAssetTagDocument owner ? owner.AuthoredAsset : item.Asset;
            foreach (var source in new SourceFilesCollector().GetSourceFiles(asset).Keys)
                sources.Add(Path.GetFullPath(source.ToOSPath(), Path.GetDirectoryName(item.FullPath.ToOSPath())!));
            if (asset.MainSource is { } main && !string.IsNullOrEmpty(main))
                sources.Add(Path.GetFullPath(main.ToOSPath(), Path.GetDirectoryName(item.FullPath.ToOSPath())!));
            if (asset is Stride.Assets.SpriteFont.SpriteFontAsset or Stride.Assets.SpriteFont.PrecompiledSpriteFontAsset)
                foreach (var fontSource in Stride.Assets.SpriteFont.FontSourcePaths.Files(asset, item.FullPath.ToOSPath())) sources.Add(fontSource);
            if (asset is Stride.Assets.Models.AnimationAsset { Type: Stride.Assets.Models.DifferenceAnimationAssetType difference }
                && !string.IsNullOrEmpty(difference.BaseSource))
                sources.Add(Path.GetFullPath(difference.BaseSource.ToOSPath(), Path.GetDirectoryName(item.FullPath.ToOSPath())!));
        }
        var paths = sources.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        return entries[root.Id] = new(WithSources(inputs, paths), roots, snapshot, paths);
    }
}
