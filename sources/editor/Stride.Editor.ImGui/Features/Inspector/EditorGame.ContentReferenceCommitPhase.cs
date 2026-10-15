namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    // Collection rendering stages structural writes until its enumeration ends.
    // The same callback, retained by a modal, must execute the real writer after that frame has ended.
    private sealed class ContentReferenceCommitPhase(Action<Action> stage, Action<Action> execute) : IDisposable
    {
        private bool drawing = true;
        internal void Submit(Action command) { if (drawing) stage(command); else execute(command); }
        public void Dispose() => drawing = false;
    }
}
