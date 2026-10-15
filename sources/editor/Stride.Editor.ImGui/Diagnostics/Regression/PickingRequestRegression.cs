using Stride.Core.Mathematics;
using Stride.Engine.Design;

namespace Stride.Editor.ImGui;

internal static class PickingRequestRegression
{
    public static void Run()
    {
        using var picker = new EditorPickingRenderer();
        var size = new Vector2(640, 480);
        picker.Request(new Vector2(0.5f), Matrix.Identity, size);
        picker.ValidateView(Matrix.Identity, size);
        Require(picker.IsPending && picker.CancelledRequests == 0, "Unchanged request view must remain pending");
        try { picker.Request(new Vector2(float.NaN), Matrix.Identity, size); throw new Exception("Invalid request must be rejected"); }
        catch (ArgumentOutOfRangeException) { }
        Require(picker.IsPending && picker.CancelledRequests == 0, "Rejected input must preserve the previous valid request");
        picker.ValidateView(Matrix.Translation(1, 0, 0), size);
        Require(!picker.IsPending && !picker.HasResult && picker.CancelledRequests == 1, "Camera change must invalidate pending picking without publishing an empty result");
        picker.Request(new Vector2(0.25f), Matrix.Identity, size);
        picker.Request(new Vector2(0.75f), Matrix.Identity, size);
        Require(picker.IsPending && picker.CancelledRequests == 2, "A newer click must supersede the pending request");
        picker.ValidateView(Matrix.Identity, new Vector2(800, 600));
        Require(!picker.IsPending && picker.CancelledRequests == 3, "Resize must invalidate old image coordinates");
        picker.Request(new Vector2(0.5f), Matrix.Identity, size); picker.Cancel("Hierarchy selection changed");
        picker.Cancel("Scene view hidden");
        Require(picker.CancelledRequests == 4 && !picker.HasResult && picker.ReadbackCount == 0 && picker.Result == null,
            "Cancellation must be idempotent and leave no stale result or GPU readback");
        Console.WriteLine("PICKING REQUEST REGRESSION PASS: view/resize invalidation, supersession, input validation and idempotent result-free cancellation without GPU work.");
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
