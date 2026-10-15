using Microsoft.CodeAnalysis;
using Stride.Assets.Scripts;

namespace Stride.Editor.ImGui;

internal sealed partial class VisualScriptDocument
{
    internal Compilation? InspectorCompilation { get; set; }
    internal void ApplyVisualInspectorChange(Action edit, string title)
    {
        ApplyVisualMutation(edit, title);
    }
    private void RegenerateInspectorBlock(Block block)
    {
        var method = Asset.Methods.SingleOrDefault(method => method.Blocks.Values.Contains(block));
        if (method == null) throw new InvalidOperationException("The block has no current function owner.");
        var slots = new List<Slot>(); block.GenerateSlots(slots, new SlotGeneratorContext(InspectorCompilation));
        ApplyGeneratedSlots(method, block, slots, withinMutation: true);
    }
}
