namespace Stride.Core.Assets;

public sealed partial class PackageSession
{
    /// <summary>Whether this session resolves assets in the namespace by unqualified URL.</summary>
    public bool IsAssetNamespaceInScope(string assetNamespace)
    {
        if (AssetNamespaceUsings.Contains(assetNamespace)) return true;
        foreach (var package in Packages)
            if (!package.IsReadOnly && package.Container?.AssetNamespace is { } candidate
                && string.Equals(candidate, assetNamespace, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
