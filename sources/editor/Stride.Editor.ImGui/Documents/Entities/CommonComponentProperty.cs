namespace Stride.Editor.ImGui;

internal sealed record CommonComponentProperty(string Key, string ComponentLabel, IReadOnlyList<ComponentField> Fields)
{
    internal ComponentField First => Fields[0];
    internal object? Value => First.Node.Retrieve();
    internal bool IsMixed => Fields.Skip(1).Any(member => !ContentAssetReference.AreEqual(member.Node.Retrieve(), Value));
    internal bool SupportsEditing => Fields.All(member => member.SupportsEditing);
}
