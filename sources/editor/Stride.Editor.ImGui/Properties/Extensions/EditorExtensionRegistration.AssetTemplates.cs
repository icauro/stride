using Stride.Core.Assets;
using Stride.Core.Assets.Templates;

namespace Stride.Editor.ImGui;

/// <summary>Explicit presentation/preparation adapter for an actual registered generator.</summary>
public sealed class EditorAssetTemplateAdapter
{
    public EditorAssetTemplateAdapter(ITemplateGenerator generator, Type assetType,
        Func<AssetTemplateGeneratorParameters, IReadOnlyList<AssetItem>> prepareAssets,
        Action<EditorAssetTemplateParameterContext>? drawParameters = null)
    {
        ArgumentNullException.ThrowIfNull(generator); ArgumentNullException.ThrowIfNull(assetType); ArgumentNullException.ThrowIfNull(prepareAssets);
        if (!typeof(Asset).IsAssignableFrom(assetType)) throw new ArgumentException("An asset template requires an actual asset type.", nameof(assetType));
        Generator = generator; AssetType = assetType; PrepareAssets = prepareAssets; DrawParameters = drawParameters;
    }
    public ITemplateGenerator Generator { get; }
    public Type AssetType { get; }
    internal Func<AssetTemplateGeneratorParameters, IReadOnlyList<AssetItem>> PrepareAssets { get; }
    internal Action<EditorAssetTemplateParameterContext>? DrawParameters { get; }
}

/// <summary>Real native parameters, selections and typed tags for a provider's ImGui wizard.</summary>
public sealed class EditorAssetTemplateParameterContext(AssetTemplateGeneratorParameters parameters, IReadOnlyList<AssetItem> selectedAssets,
    Func<AssetItem, AssetItem>? snapshotAsset = null)
{
    public AssetTemplateGeneratorParameters Parameters { get; } = parameters;
    public IReadOnlyList<AssetItem> SelectedAssets { get; } = selectedAssets;
    public IEnumerable<AssetItem> FindAssets(params Type[] acceptedTypes) => Parameters.Package.Session.Packages
        .Where(package => package.State >= PackageState.AssetsReady).SelectMany(package => package.Assets)
        .Where(item => acceptedTypes.Any(type => type.IsInstanceOfType(item.Asset)))
        .Select(item => snapshotAsset?.Invoke(item) ?? item.Clone(true, newAsset: AssetCloner.Clone(item.Asset)));
}

public sealed partial class EditorExtensionRegistration
{
    internal readonly List<EditorAssetTemplateAdapter> AssetTemplates = [];
    public void RegisterAssetTemplateAdapter(EditorAssetTemplateAdapter adapter)
    { Check(); ArgumentNullException.ThrowIfNull(adapter); AssetTemplates.Add(adapter); }
}
