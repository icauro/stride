using Stride.Core.Assets;
using Stride.Core.IO;

namespace Stride.Editor.ImGui;

internal sealed record PreviewFamilyFixtureVerification(string Family, string AssetName, string AssetPath, AssetId AssetId, string SourceRoot);

internal sealed partial class EditorGame
{
    internal PreviewFamilyFixtureVerification PreparePreviewFamilyFixture(string family)
    {
        if (workspace != null || projectLoading != null) throw new InvalidOperationException("Prepare preview family before project loading.");
        var packagePath = Path.GetFullPath(projectPathInput);
        var privateRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Stride-ImGui-UI")) + Path.DirectorySeparatorChar;
        if (!packagePath.StartsWith(privateRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Preview family fixture must be a private disposable UI copy.");
        string? sourceRoot = null;
        var explicitSource = Array.IndexOf(args, "--preview-catalog-fixture");
        if (explicitSource >= 0)
        {
            if (explicitSource + 1 >= args.Length) throw new ArgumentException("--preview-catalog-fixture requires a real catalog directory.");
            sourceRoot = Path.GetFullPath(args[explicitSource + 1]);
            if (!Directory.Exists(sourceRoot)) throw new DirectoryNotFoundException(sourceRoot);
        }
        for (var ancestor = new DirectoryInfo(AppContext.BaseDirectory); sourceRoot == null && ancestor != null; ancestor = ancestor.Parent)
        {
            foreach (var prefix in new[] { ancestor.FullName, Path.Combine(ancestor.FullName, "bin", "editor-imgui") })
            {
                var candidate = Path.Combine(prefix, "visual-capture", "component-catalog-review", "asset-catalog-fixture");
                if (Directory.Exists(candidate)) { sourceRoot = candidate; break; }
            }
        }
        if (sourceRoot == null) throw new DirectoryNotFoundException("Real preview-family catalog fixture is unavailable beneath the executable's ancestor directories.");
        // Catalog sources are actual imported resources, not generated readiness substitutes.
        var selected = family switch
        {
            "animation" => "Catalog/Models/Animation/291c7c59abd3/Run.sdanim",
            "sprite" => "Catalog/UI/SpriteSheet/96f96bf3021b/ParticleButtons.sdsheet",
            "font" => "Catalog/UI/SpriteFont/41859a76bf92/CatalogOriginalFont.sdfnt",
            _ => throw new ArgumentOutOfRangeException(nameof(family)),
        };
        var required = new List<string> { "Assets/" + selected };
        if (family == "animation")
        {
            required.AddRange(new[] {
                "Assets/Catalog/Models/Model/8c7c72efc1b9/Character.sdm3d",
                "Assets/Catalog/Models/Skeleton/e49dbd54e03b/ModelSkeleton.sdskel",
                "Assets/Catalog/Rendering/Material/6dabc50246ef/BodyWhite.sdmat",
                "Assets/Catalog/Rendering/Material/0d1e74b834ea/Emissive.sdmat",
                "Assets/Catalog/Rendering/Material/c2456248c184/BodyGray.sdmat",
                "Assets/Catalog/Rendering/Material/154e9aa0106f/MattBlackUpper.sdmat",
                "Assets/Catalog/Rendering/Material/45c3922f0061/MattBlackLower.sdmat",
                "Assets/Catalog/Rendering/Texture/2bc369c1dcd9/normalmapUpper.sdtex",
                "Assets/Catalog/Rendering/Texture/0b1c4d887f93/normalmapLower.sdtex",
                "Resources/Catalog/cadf6e475e5f/normalmapUpper.png",
                "Resources/Catalog/cadf6e475e5f/normalmapLower.png",
                "Resources/Catalog/065cea141bb1/Run.fbx",
                "Resources/Catalog/a8f2a1726edd/mannequinModel.fbx" });
        }
        else required.Add(family == "sprite" ? "Resources/Catalog/5856fed66089/buttons.png"
            : "Resources/Catalog/24cd71ef838e/Risaltyp_024.ttf");
        foreach (var path in required)
            if (!File.Exists(Path.Combine(sourceRoot, path)))
                throw new FileNotFoundException("The real catalog preview prerequisite is missing; this case cannot pass without it.", Path.Combine(sourceRoot, path));
        var root = Path.GetDirectoryName(packagePath)!;
        if (family == "font")
        {
            const string licensePath = "Resources/Catalog/24cd71ef838e/Risaltyp_024_License.txt";
            var sourceLicense = Path.Combine(sourceRoot, licensePath);
            if (File.Exists(sourceLicense))
            {
                var destinationLicense = Path.Combine(root, licensePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationLicense)!);
                File.Copy(sourceLicense, destinationLicense, false);
            }
        }
        var package = AssetFileSerializer.Load<Package>(packagePath).Asset;
        var packageOwner = package.Container ?? new StandalonePackage(package);
        foreach (var relative in required)
        {
            var destination = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(sourceRoot, relative), destination, false);
            if (!relative.StartsWith("Assets/", StringComparison.Ordinal)) continue;
            // Load and save through native serializers, and root every real dependency explicitly.
            var asset = AssetFileSerializer.Load<Asset>(destination).Asset;
            AssetFileSerializer.Save(destination, asset, null);
            package.RootAssets.Add(new AssetReference(asset.Id, packageOwner.Qualify(new UFile(Path.ChangeExtension(relative[7..], null)!))));
        }
        AssetFileSerializer.Save(packagePath, package, null);
        var mainPath = Path.Combine(root, "Assets", selected);
        return new(family, Path.GetFileNameWithoutExtension(selected), mainPath,
            AssetFileSerializer.Load<Asset>(mainPath).Asset.Id, sourceRoot);
    }
}
