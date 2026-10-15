using Stride.Assets.Scripts;
using Stride.Core.Assets.Quantum;
using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

internal sealed partial class VisualScriptDocument
{
    internal override IEditorNativeMutationObserver BeginNativeAuthoringMutation() => new BlockMutationObserver(this);

    private sealed class BlockMutationObserver : IEditorNativeMutationObserver
    {
        private readonly VisualScriptDocument document;
        private readonly List<AssetGraphNodeChangeListener> listeners = [];
        private readonly HashSet<Block> regenerate = new(ReferenceEqualityComparer.Instance);
        private bool changed, completing, completed;

        internal BlockMutationObserver(VisualScriptDocument document)
        {
            this.document = document;
            try
            {
                foreach (var block in document.Asset.Methods.SelectMany(method => method.Blocks.Values))
                {
                    var listener = new AssetGraphNodeChangeListener(document.ObjectNode(block), document.AuthoringGraph.Definition);
                    listeners.Add(listener);
                    listener.ValueChanged += (_, args) =>
                    {
                        if (completing) return;
                        changed = true;
                        if (args.Member.MemberDescriptor.GetCustomAttributes<RegenerateSlotsAttribute>(true).Any()) regenerate.Add(block);
                    };
                    listener.ItemChanged += (_, _) => { if (!completing) changed = true; };
                    listener.Initialize();
                }
                // Function topology and declaration changes also affect generated source.
                document.AuthoringGraph.Changed += GraphChanged;
                document.AuthoringGraph.ItemChanged += GraphItemChanged;
            }
            catch { Dispose(); throw; }
        }
        private void GraphChanged(object? sender, AssetMemberNodeChangeEventArgs args) { if (!completing) changed = true; }
        private void GraphItemChanged(object? sender, AssetItemNodeChangeEventArgs args) { if (!completing) changed = true; }
        public void Complete()
        {
            if (completed) return;
            completing = true;
            try
            {
                foreach (var block in regenerate)
                    if (document.Asset.Methods.Any(method => method.Blocks.Values.Contains(block))) document.RegenerateInspectorBlock(block);
                if (changed) document.UpdateGeneratedOutput?.Invoke(document);
                completed = true;
            }
            finally { completing = false; }
        }
        public void Dispose()
        {
            document.AuthoringGraph.Changed -= GraphChanged;
            document.AuthoringGraph.ItemChanged -= GraphItemChanged;
            foreach (var listener in listeners.ToArray()) { listener.Dispose(); listeners.Remove(listener); }
        }
    }
}
