using Stride.Assets.Entities;
using Stride.Assets.Textures;
using Stride.Core.Assets;
using Stride.Core.IO;

namespace Stride.Editor.ImGui;

internal static class PollingAssetPathVerification
{
    internal static object Verify()
    {
        // These objects are independent native items, never published into the
        // running session. Paths are inputs only; no filesystem writes occur.
        var root = Path.Combine(Path.GetTempPath(), "Stride-PollingPath", Guid.NewGuid().ToString("N"));
        var package = new Package { FullPath = new UFile(Path.Combine(root, "first", "Fixture.sdpkg")), AssetNamespace = "Native" };
        var container = new StandalonePackage(package) { AssetNamespace = "Native" };
        var item = AssetItem.CreateForPackage(package, new UFile("Scenes/Original"), new SceneAsset());
        var cases = new List<string>();
        void Check(string name, Action? change = null)
        {
            change?.Invoke();
            var native = item.FullPath;
            var cached = AuthoredImportPollingCache.CaptureAssetPath(item);
            if (!string.Equals(cached.Path, native.ToString(), StringComparison.Ordinal)
                || !string.Equals(cached.OsPath, native.ToOSPath(), StringComparison.Ordinal))
                throw new InvalidOperationException("Derived polling path differs from native FullPath: " + name);
            var warm = AuthoredImportPollingCache.CaptureAssetPath(item);
            if (!ReferenceEquals(cached.Path, warm.Path) || !ReferenceEquals(cached.OsPath, warm.OsPath))
                throw new InvalidOperationException("Unchanged native path inputs must reuse derived strings: " + name);
            cases.Add(name);
        }
        void SetNative(string property, object? value)
            => typeof(AssetItem).GetProperty(property)!.SetValue(item, value);
        Check("Initial qualified native scene path");
        Check("Relative explicit source folder", () => item.SourceFolder = new UDirectory("Content"));
        Check("Absolute explicit source folder", () => item.SourceFolder = new UDirectory(Path.Combine(root, "external")));
        Check("Default source folder fallback", () => item.SourceFolder = null);
        Check("First package asset folder", () => package.AssetFolders.Add(new AssetFolder(new UDirectory("FirstAssets"))));
        Check("Mutable default folder path", () => package.AssetFolders[0].Path = new UDirectory("ChangedAssets"));
        Check("Root relocation", () => package.FullPath = new UFile(Path.Combine(root, "second", "Fixture.sdpkg")));
        Check("Native location relocation", () => SetNative(nameof(AssetItem.Location), new UFile("/Native/Scenes/Relocated")));
        Check("Case-only native location change", () => SetNative(nameof(AssetItem.Location), new UFile("/Native/scenes/relocated")));
        Check("Container namespace replacement", () => container.AssetNamespace = "Other");
        Check("Null container namespace uses package namespace", () => container.AssetNamespace = null);
        Check("Empty container namespace remains authoritative", () => container.AssetNamespace = "");
        Check("Package namespace mutation", () => { container.AssetNamespace = null; package.AssetNamespace = "Other"; });
        Check("Relative alternative path", () => item.AlternativePath = new UFile("Linked/Explicit.sdscene"));
        Check("Absolute alternative path", () => item.AlternativePath = new UFile(Path.Combine(root, "linked", "Explicit.sdscene")));
        Check("Alternative path removal", () => item.AlternativePath = null);
        Check("Native asset type changes default extension", () => SetNative(nameof(AssetItem.Asset), new TextureAsset()));
        var otherPackage = new Package { FullPath = new UFile(Path.Combine(root, "third", "Other.sdpkg")) };
        Check("Native package owner replacement", () => SetNative(nameof(AssetItem.Package), otherPackage));
        Check("Unattached native item", () => SetNative(nameof(AssetItem.Package), null));
        return new { cases = cases.ToArray(), count = cases.Count, exactOrdinalNativePathEquality = true,
            unchangedDerivedStringIdentity = true, filesystemWrites = false,
            sameTypeRegistryExtensionMutationExercised = false,
            boundary = "Independent native AssetItem.FullPath oracle after actual native input mutations; compiler freshness and disk stat invalidation are covered separately." };
    }
}
