using Stride.Core.Diagnostics;
using Stride.Core.Mathematics;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private object? sceneGenerationParityEvidence;
    private sealed record SceneGenerationCase(EditorDocumentEntry Entry, EntityHierarchyDocument Before,
        EditorAuthoringState BeforeState, EngineViewport View, Matrix Camera, Guid[] Entities, Guid[] Selection,
        byte[] Disk, bool Dirty);

    // Private-fixture arrangement calls the real canonical generation factory,
    // swap journal and native history replay; it does not emulate OS gestures.
    private void VerifyTwoViewSceneGenerationHistoryParity(EditorDocumentEntry first, EditorDocumentEntry second)
    {
        if (!Environment.GetCommandLineArgs().Contains("--ui-test") || sessionHistory == null)
            throw new InvalidOperationException("Scene generation replay requires a private loaded UI fixture.");
        var cases = new[] { first, second }.Select(entry =>
        {
            var owner = entry.Document as EntityHierarchyDocument
                ?? throw new InvalidOperationException("Generation replay needs actual hierarchy documents.");
            var view = EnsureSceneDocumentView(entry);
            return new SceneGenerationCase(entry, owner, entry.State!, view.Viewport, view.Viewport.Camera.Component.ViewMatrix,
                owner.Asset.Hierarchy.Parts.Keys.Order().ToArray(), owner.SelectedIds.ToArray(), File.ReadAllBytes(entry.Path), owner.IsDirty);
        }).ToArray();
        if (ReferenceEquals(cases[0].View, cases[1].View)
            || ReferenceEquals(cases[0].View.RuntimeGame, cases[1].View.RuntimeGame))
            throw new InvalidOperationException("Generation replay requires independent real scene authorities.");
        var after = new EditorAuthoringState[2];
        var stages = new List<object>();
        var applied = 0;
        void Check(string stage, bool successors)
        {
            foreach (var item in cases)
            {
                var index = Array.IndexOf(cases, item);
                var expected = successors ? after[index] : item.BeforeState;
                var owner = item.Entry.Document as EntityHierarchyDocument;
                if (!ReferenceEquals(item.Entry.State, expected) || !ReferenceEquals(item.Entry.Document, expected.Document)
                    || !IsCurrentDocumentPresentation(item.Entry)
                    || !sceneDocumentViews.TryGetValue(item.Entry.Id, out var view) || !IsCurrentSceneDocumentView(view)
                    || !ReferenceEquals(view.Viewport, item.View) || !ReferenceEquals(view.Document, owner)
                    || !ReferenceEquals(view.State, expected) || !ReferenceEquals(view.Viewport.PresentedDocument, owner)
                    || view.Viewport.RuntimeScene == null || view.Viewport.Camera.Component.ViewMatrix != item.Camera
                    || owner == null || !item.Entities.SequenceEqual(owner.Asset.Hierarchy.Parts.Keys.Order())
                    || !item.Selection.SequenceEqual(owner.SelectedIds)
                    || !item.Disk.SequenceEqual(File.ReadAllBytes(item.Entry.Path)))
                    throw new InvalidOperationException(stage + ": actual scene generation/view/state/camera/identities/disk certificate failed.");
                if (!successors && owner.IsDirty != item.Dirty)
                    throw new InvalidOperationException(stage + ": native Undo did not restore the authored dirty/savepoint state.");
            }
            if (ReferenceEquals(cases[0].View.RuntimeScene, cases[1].View.RuntimeScene)
                || ReferenceEquals(cases[0].View.RuntimeGame, cases[1].View.RuntimeGame))
                throw new InvalidOperationException(stage + ": independent native authorities aliased.");
            stages.Add(new { stage, entryIds = cases.Select(item => item.Entry.Id).ToArray(), nativeCurrentViews = true,
                independentAuthorities = true, cameraSelectionAndDiskPreserved = true });
        }
        try
        {
            for (var index = 0; index < cases.Length; index++)
            {
                var item = cases[index];
                var snapshot = item.Before.CaptureAssemblyReload();
                ReplaceAuthoredDocument(item.Entry, () => item.Before.CreateAssemblyReloadedGeneration(snapshot, new LoggerResult()),
                    "Verify canonical scene generation " + index);
                after[index] = item.Entry.State!; applied++;
                if (ReferenceEquals(after[index], item.BeforeState))
                    throw new InvalidOperationException("The real canonical generation factory did not publish a successor.");
            }
            Check("generation-swap", true);
            sessionHistory.Undo(); applied--; sessionHistory.Undo(); applied--;
            Check("native-undo", false);
            sessionHistory.Redo(); applied++; sessionHistory.Redo(); applied++;
            Check("native-redo", true);
            sessionHistory.Undo(); applied--; sessionHistory.Undo(); applied--;
            Check("final-native-undo", false);
            var loadCompensation = VerifySceneLoadCompensationParity(cases[0], cases[1]);
            sceneGenerationParityEvidence = new { targetCount = 2, stages,
                realCanonicalGenerationFactory = true, actualHistoryUndoRedo = true,
                loadCompensation, failedInitialLoadCompensationCovered = true, physicalInputCovered = false };
        }
        finally
        {
            while (applied > 0) { sessionHistory.Undo(); applied--; }
        }
    }
}
