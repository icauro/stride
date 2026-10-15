using Stride.Core;
using Stride.Core.Quantum;
using Stride.Core.Reflection;
using Stride.Assets.Sprite;

namespace Stride.Editor.ImGui;

internal sealed record SpriteSettingField(IMemberNode Node, string Key, string Label, string Category)
{
    public Type Type => Node.Type;
    public PropertyNumericRange? DeclarationNumericRange { get; } = AssetSettingNumericMetadata.Read(Node);
    public IReadOnlyList<PropertyNumericRange?> DeclarationAxisRanges { get; } = VectorPropertyNumericMetadata.Create(Node);
    public string[] EnumNames { get; } = Node.Type.IsEnum ? Enum.GetNames(Node.Type) : [];
    public string[] EnumLabels { get; } = Node.Type.IsEnum ? Enum.GetNames(Node.Type).Select(name =>
        TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<DisplayAttribute>(Node.Type.GetField(name)!)?.Name ?? name).ToArray() : [];
}

internal sealed record SpriteSettingFrame(ItemId Id, SpriteInfo Value, IReadOnlyList<SpriteSettingField> Fields, IMemberNode BorderNode);
