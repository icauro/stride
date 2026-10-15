using Stride.Assets.Scripts;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool TryVisualScriptPresentationCommand(VisualScriptDocument source, Method method, Action command)
    {
        var entry = documents.Find(source); var state = entry?.State;
        var expectedWorkspace = workspace; var expectedHistory = sessionHistory;
        bool Current() => entry != null && ReferenceEquals(entry.Document, source) && ReferenceEquals(entry.State, state)
            && ReferenceEquals(workspace, expectedWorkspace) && ReferenceEquals(sessionHistory, expectedHistory)
            && CanMutateDocumentPresentation(source) && source.CanSave
            && source.Asset.Methods.Any(current => ReferenceEquals(current, method));
        return Services.Commands.TryExecute(() =>
        {
            if (!Current()) throw new InvalidOperationException("The visual-script presentation is no longer active.");
            ValidatePendingPropertyStrings();
            if (!Current()) throw new InvalidOperationException("The visual-script owner changed while finishing pending edits.");
            command();
        });
    }
}
