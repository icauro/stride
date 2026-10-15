using Stride.Core.Mathematics;

namespace Stride.Editor.ImGui;

internal static class CameraProjectionRegression
{
    public static void Run()
    {
        var camera = new EditorViewportCamera(); camera.Component.Update(1.5f);
        var view = camera.Component.ViewMatrix; var perspective = camera.Component.ProjectionMatrix;
        camera.Orthographic = true; camera.OrthographicSize = 10; camera.Component.Update(1.5f);
        Require(camera.Component.ViewMatrix == view && camera.Component.ProjectionMatrix != perspective, "Projection mode must change the real camera projection without moving its view");
        var position = camera.Position;
        camera.Navigate(Vector2.Zero, 1, false, false, false, false, false, Vector3.Zero, 0.016f);
        Require(camera.Position == position && camera.OrthographicSize < 10, "Orthographic wheel zoom must change size, not camera distance");
        camera.OrthographicSize = -1; Require(camera.OrthographicSize > 0, "Orthographic size must remain positive");
        try { camera.OrthographicSize = float.NaN; throw new Exception("Nonfinite size must be rejected"); } catch (ArgumentException) { }
        camera.Component.Update(0.5f); camera.Focus(Vector3.Zero, 2);
        Require(camera.OrthographicSize >= 8, "Orthographic framing must account for a narrow viewport aspect ratio");
        camera.Component.Update(0.5f);
        Require(float.IsFinite(camera.Component.ViewProjectionMatrix.M11), "Framed orthographic projection must be finite");
        var target = camera.Target; var radius = camera.Radius; var size = camera.OrthographicSize;
        foreach (var orientation in EditorViewportCamera.Orientations)
        {
            camera.Orient(orientation); camera.Component.Update(0.5f);
            var direction = Vector3.Normalize(camera.Target - camera.Position);
            Require(Vector3.Distance(direction, EditorViewportCamera.Direction(orientation)) < 0.00001f
                && camera.Target == target && camera.Radius == radius && camera.OrthographicSize == size
                && float.IsFinite(camera.Component.ViewMatrix.Determinant()), "Every named orientation must preserve pivot/distance/zoom and produce a finite camera matrix");
        }
        camera.Orthographic = false; camera.Reset(); camera.Component.Update(1.5f);
        Require(camera.Component.ProjectionMatrix == perspective && camera.Component.ViewMatrix == view, "Switch/reset must recover perspective projection and view");
        Console.WriteLine("CAMERA PROJECTION REGRESSION PASS: real projection switching, position-preserving orthographic zoom, positive finite size and aspect-aware framing.");
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
