using Stride.Assets;
using Stride.Assets.Presentation.NodePresenters.Rules;
using Stride.Core;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Rules;
using Stride.Core.Annotations;
using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Core.Diagnostics;
using Stride.Core.Extensions;
using Stride.Core.Quantum;
using Stride.Core.Reflection;
using Stride.Data;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    private AssetItem? gameSettingsItem;
    private AssetPropertyGraph? gameSettingsGraph;
    private readonly LoggerResult gameSettingsLog = new();
    private readonly HashSet<GameSettingField> gameSettingMembers = new(ReferenceEqualityComparer.Instance);
    private IReadOnlyList<GameSettingField> gameSettingFields = [];
    private IReadOnlyList<GameSettingConfiguration> gameSettingConfigurations = [];
    // Retained text drafts live in the shared controller; accepted values are
    // journaled immediately and contribute to the canonical document revision.
    private bool HasGameSettingChange => false;
    public GameSettingsAsset? GameSettings => AuthoredAsset as GameSettingsAsset;
    public bool CanEditGameSettings => CanEditArchetypeAsset && (GameSettings != null || HasVisualScriptInspector);
    public IReadOnlyList<GameSettingField> GameSettingFields { get { EnsureGameSettingsGraph(); return gameSettingFields; } }
    public IReadOnlyList<GameSettingConfiguration> GameSettingConfigurations { get { EnsureGameSettingsGraph(); return gameSettingConfigurations; } }
    public IReadOnlyList<Type> AvailableGameConfigurations => typeof(Configuration).GetInheritedInstantiableTypes()
        .Where(type => !type.IsDefined(typeof(NonInstantiableAttribute), true) && ObjectFactoryRegistry.CanCreateInstance(type)
            && GameSettingsConfigurationRules.CanAdd(GameSettings!.Defaults, type))
        .OrderBy(GameSettingsConfigurationRules.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    private void InitializeGameSettings()
    {
        EnsureGameSettingsGraph();
        if (CanEditGameSettings && GameSettings!.TryGet<Stride.Graphics.RenderingSettings>() == null)
            AddGameConfigurationCore(typeof(Stride.Graphics.RenderingSettings), required: true);
    }
    private void EnsureGameSettingsGraph()
    {
        if (gameSettingsGraph != null || GameSettings == null) return;
        gameSettingsGraph = EnsureSettingsAuthoringGraph(out gameSettingsItem);
        RefreshGameSettingsFields();
        InitializeGameOrientationBaseline();
    }
    private void RefreshGameSettingsFields()
    {
        gameSettingItems.Clear(); gameSettingItemMembers.Clear(); rootGameSettingCollections.Clear(); nestedGameSettingCollections.Clear();
        gameSettingMembers.Clear();
        if (HasVisualScriptInspector) { RefreshVisualScriptInspectorFields(); return; }
        gameSettingFields = ReadGameSettingsFields(gameSettingsGraph!.RootNode, "", 0, new HashSet<IObjectNode>());
        var defaultsMember = gameSettingsGraph.RootNode[nameof(GameSettingsAsset.Defaults)];
        var defaults = (IAssetObjectNode)defaultsMember.Target!;
        var defaultsOrder = TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<DisplayAttribute>(defaultsMember.MemberDescriptor.MemberInfo)?.Order
            ?? defaultsMember.MemberDescriptor.Order;
        gameSettingConfigurations = (defaults.Indices ?? []).Select(index => new GameSettingConfiguration(index,
            defaults.TryIndexToId(index, out var id) ? id : null, (Configuration)defaults.Retrieve(index)!,
            GameSettingsConfigurationRules.Name(defaults.Retrieve(index)?.GetType()), GameSettingsConfigurationRules.Order(defaultsOrder, index.Int),
            ReadGameSettingsFields(defaults.IndexedTarget(index)!, "Defaults." + index.Int, 0, new HashSet<IObjectNode>()))).ToArray();
    }
    private IReadOnlyList<GameSettingField> ReadGameSettingsFields(IObjectNode target, string prefix, int depth, HashSet<IObjectNode> ancestors,
        Func<IMemberNode, bool>? rootMemberFilter = null)
    {
        // An unloadable object serializes its original ParsingEvents. Its proxy
        // base members are not the authored fields described by that stream.
        if (target.Retrieve() is Stride.Core.Yaml.IUnloadable || depth >= 16 || !ancestors.Add(target)) return [];
        var fields = new List<GameSettingField>();
        foreach (var node in target.Members)
        {
            if (rootMemberFilter != null && !rootMemberFilter(node)) continue;
            // Registered GameSettings subtypes use the same native root property
            // provider. Serialized descriptors and native visibility, rather than
            // the declaring type of the stock asset, select their authored fields.
            var display = TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<DisplayAttribute>(node.MemberDescriptor.MemberInfo);
            if (display?.Browsable == false) continue;
            if (NavigationNodeRules.BypassMember(target.Type, node.Name) && node.Target is { IsEnumerable: false } bypass)
            {
                fields.AddRange(ReadGameSettingsFields(bypass, prefix.Length == 0 ? node.Name : prefix + "." + node.Name, depth + 1, ancestors));
                continue;
            }
            var descriptor = new GameSettingField(node, prefix.Length == 0 ? node.Name : prefix + "." + node.Name,
                display?.Name ?? Stride.Core.Presentation.Core.Utils.SplitCamelCase(node.Name), display?.Category ?? "", display?.Order ?? node.MemberDescriptor.Order ?? 0);
            if (!IsGameSettingScalar(descriptor.Type) && !ContentAssetReference.Supports(descriptor.Type) && !SceneObjectReference.Supports(descriptor.Type)
                && node.Target is { IsEnumerable: false } child)
                descriptor = descriptor with { Children = ReadGameSettingsFields(child, descriptor.Key, depth + 1, ancestors) };
            fields.Add(descriptor); gameSettingMembers.Add(descriptor);
        }
        ancestors.Remove(target); return fields.OrderBy(member => member.Order).ToArray();
    }
    private static bool IsGameSettingScalar(Type type) => type.IsPrimitive || type.IsEnum || type == typeof(decimal) || type == typeof(string)
        || type == typeof(Stride.Core.IO.UFile) || type == typeof(Stride.Core.IO.UDirectory)
        || type == typeof(Stride.Core.Mathematics.Color) || type == typeof(Stride.Core.Mathematics.Color3) || type == typeof(Stride.Core.Mathematics.Color4)
        || type == typeof(Stride.Core.Mathematics.Vector2) || type == typeof(Stride.Core.Mathematics.Vector3) || type == typeof(Stride.Core.Mathematics.Vector4)
        || type == typeof(Stride.Core.Mathematics.Int2) || type == typeof(Stride.Core.Mathematics.Int3) || type == typeof(Stride.Core.Mathematics.Int4)
        || type == typeof(Guid) || type == typeof(TimeSpan) || type == typeof(DateTime)
        || type == typeof(Stride.Core.Mathematics.AngleSingle) || type == typeof(Stride.Core.Mathematics.Rectangle)
        || type == typeof(Stride.Core.Mathematics.RectangleF)
        || type == typeof(Stride.Core.Mathematics.UInt4) || type == typeof(Stride.Core.Mathematics.Matrix) || type == typeof(Stride.Core.Mathematics.Quaternion);

    public void UpdateGameSetting(GameSettingField field, object? value)
        => UpdateGameFieldValue(field, value);
    public void EndGameSettingEdit() { }
    public void ChangeGameSettingType(GameSettingField field, Type? type)
    {
        CommitEdits();
        if (ExtensionAbstractEntries(field.Node) != null)
        {
            var entry = NativeAbstractEntries.TypeEntry(GameSettingEntries(field), type) ?? throw new InvalidOperationException("This native property type cannot be selected.");
            ChangeGameSettingEntry(field, entry); return;
        }
        if (type != null && !field.Types.Contains(type)) throw new ArgumentException("This type is not compatible.");
        ChangeGameSettingOwnedValue(field, type == null ? null : ObjectFactoryRegistry.NewInstance(type));
    }
    public void ChangeNullableGameSettingPresence(GameSettingField field, bool present)
    {
        CommitEdits();
        var type = Nullable.GetUnderlyingType(field.Node.Type) ?? throw new ArgumentException("This setting is not nullable.");
        ChangeGameSettingOwnedValue(field, present ? ObjectFactoryRegistry.NewInstance(type) : null);
    }
    private void ChangeGameSettingOwnedValue(GameSettingField field, object? value)
        => UpdateGameFieldValue(field, value);
    private IAssetObjectNode EditableGameConfigurations()
    {
        EnsureGameSettingsGraph(); CommitEdits();
        if (!CanEditGameSettings) throw new InvalidOperationException("This asset can't be edited.");
        var member = gameSettingsGraph!.RootNode[nameof(GameSettingsAsset.Defaults)];
        var node = (IAssetObjectNode)member.Target!;
        if (IsExtensionPropertyReadOnly(member) || !CanEditAssetSettingNode(node)) throw new InvalidOperationException("These configurations cannot be edited.");
        return node;
    }
    public void AddGameConfiguration(Type type) => AddGameConfigurationCore(type, required: false);
    private void AddGameConfigurationCore(Type type, bool required)
    {
        EditableGameConfigurations();
        if (!(required && type == typeof(Stride.Graphics.RenderingSettings) && GameSettingsConfigurationRules.CanAdd(GameSettings!.Defaults, type)
            && ObjectFactoryRegistry.CanCreateInstance(type)) && !AvailableGameConfigurations.Contains(type))
            throw new ArgumentException("This configuration already exists or cannot be created.");
        var value = ObjectFactoryRegistry.NewInstance(type) as Configuration ?? throw new InvalidOperationException("The configuration factory returned an incompatible value.");
        if (value.GetType() != type) throw new InvalidOperationException("The configuration factory returned an incompatible type.");
        var field = gameSettingFields.First(member => member.Node.Name == nameof(GameSettingsAsset.Defaults));
        var collection = GetGameSettingCollection(field) ?? throw new InvalidOperationException("The configuration collection is unavailable.");
        AddPreparedGameSettingItem(collection, value, null, "Add configuration");
    }
    public void RemoveGameConfiguration(GameSettingConfiguration configuration)
    {
        EditableGameConfigurations();
        var item = CurrentGameConfigurationItem(configuration)
            ?? throw new InvalidOperationException("This configuration no longer exists.");
        ChangeGameSettingCollectionItem(item, node => node.Remove(item.Value!, item.Index), "Remove configuration", resize: true);
    }
    private void PrepareGameSettingsForSave()
    {
        if (gameSettingsGraph == null) return;
        gameSettingsGraph.PrepareForSave(gameSettingsLog, gameSettingsItem!);
        if (gameSettingsLog.HasErrors) throw new InvalidOperationException(gameSettingsLog.ToText());
    }
    private void UpdateGameSettingsPath(string path) => DocumentAssetPath.Update(gameSettingsItem, path);
    private void DisposeGameSettings() => gameSettingsGraph?.Dispose();
}
