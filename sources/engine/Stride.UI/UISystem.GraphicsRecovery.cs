namespace Stride.UI
{
    public partial class UISystem
    {
        public bool TryReleaseGraphicsResourcesForRecovery()
        {
            if (Batch != null) { Batch.Dispose(); Batch = null; }
            UIElementUnderMouseCursor = null; FocusedElement = null;
            pendingPointerEvents.Clear();
            return true;
        }
        public void RebindGraphicsResourcesAfterRecovery()
        {
            if (Batch == null) LoadContent();
        }
    }
}
