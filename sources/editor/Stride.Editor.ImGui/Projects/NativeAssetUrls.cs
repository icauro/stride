using Stride.Core.Assets;
using Stride.Core.IO;

namespace Stride.Editor.ImGui;

internal static class NativeAssetUrls
{
    internal static string Qualify(AssetItem item) => Owner(item.Package).Qualify(item.Location).ToString();
    internal static string Unqualify(Package package, UFile location) => Owner(package).Unqualify(location).ToString();

    private static PackageContainer Owner(Package? package)
    {
        var assetNamespace = package?.Container?.AssetNamespace ?? package?.AssetNamespace;
        return package?.Container is { } owner && owner.AssetNamespace == assetNamespace ? owner
            : new StandalonePackage(new Package()) { AssetNamespace = assetNamespace };
    }
}
