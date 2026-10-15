using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorExtensionSession
{
    private (Provider Owner, Action<AssetItem>? Open, Func<AssetItem, IDisposable>? OwnedOpen)? ResolveAssetEditor(Type assetType)
    {
        // Original PluginService/TypeHelpers walks the concrete CLR base chain.
        for (var candidate = assetType; candidate != null && typeof(Asset).IsAssignableFrom(candidate); candidate = candidate.BaseType)
        {
            var matches = providers.Where(provider => !provider.Retiring && provider.Available
                && !IsProviderGenerationUnloading(provider.Type) && (provider.Registration.Editors.ContainsKey(candidate) || provider.Registration.OwnedEditors.ContainsKey(candidate))).ToArray();
            if (matches.Length > 1) throw new InvalidOperationException("Multiple extension editors handle the same nearest asset type.");
            if (matches.Length == 1) return (matches[0], matches[0].Registration.Editors.GetValueOrDefault(candidate), matches[0].Registration.OwnedEditors.GetValueOrDefault(candidate));
        }
        return null;
    }
    private bool OpenResolvedAssetEditor(AssetItem item)
    {
        var match = ResolveAssetEditor(item.Asset.GetType());
        if (match == null) return false;
        var owner = match.Value.Owner;
        if (match.Value.OwnedOpen != null) return OpenOwnedAssetEditor(item, owner, match.Value.OwnedOpen);
        owner.Leases++;
        try
        {
            if (owner.Retiring || !owner.Available || IsProviderGenerationUnloading(owner.Type))
                throw new InvalidOperationException("The asset editor generation is no longer available.");
            match.Value.Open!(item);
            return true;
        }
        finally { owner.Leases--; }
    }
}
