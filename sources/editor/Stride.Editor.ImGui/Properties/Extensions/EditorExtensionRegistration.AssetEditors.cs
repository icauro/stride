using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

public sealed partial class EditorExtensionRegistration
{
    internal readonly Dictionary<Type, Func<AssetItem, IDisposable>> OwnedEditors = [];
    /// <summary>Returns the actual editor resource whose disposal closes its view and releases its native owners.</summary>
    public void RegisterOwnedAssetEditor(Type assetType, Func<AssetItem, IDisposable> open)
    {
        Check(); ArgumentNullException.ThrowIfNull(assetType); ArgumentNullException.ThrowIfNull(open);
        if (!typeof(Asset).IsAssignableFrom(assetType)) throw new ArgumentException("Editor registration requires an asset type.", nameof(assetType));
        if (Editors.ContainsKey(assetType)) throw new InvalidOperationException("An asset editor is already registered for this type.");
        OwnedEditors.Add(assetType, open);
    }
}
