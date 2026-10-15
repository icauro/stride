namespace Stride.Editor.ImGui;

internal sealed partial class EngineViewport
{
    internal object CaptureMatchedBenchmarkState()
    {
        if (!Environment.GetCommandLineArgs().Contains("--matched-benchmark"))
            throw new InvalidOperationException("Native benchmark inspection requires --matched-benchmark.");
        var authority = sceneAuthority ?? throw new InvalidOperationException("The actual scene authority is unavailable.");
        var native = authority.ViewGame;
        var target = Target ?? throw new InvalidOperationException("The actual scene target is unavailable.");
        var camera = Camera.Component;
        var matrix = camera.ViewMatrix;
        var projection = camera.ProjectionMatrix;
        return new { assetId = PresentedDocument?.Asset.Id.ToString(), targetWidth = target.Width, targetHeight = target.Height,
            updateFrame = native.UpdateTime.FrameCount, drawFrame = native.DrawTime.FrameCount,
            updateSeconds = native.UpdateTime.Total.TotalSeconds, drawSeconds = native.DrawTime.Total.TotalSeconds,
            visible = Visible, authorityStopping = authority.IsStopping,
            cameraPosition = new[] { Camera.Position.X, Camera.Position.Y, Camera.Position.Z },
            viewMatrix = new[] { matrix.M11, matrix.M12, matrix.M13, matrix.M14, matrix.M21, matrix.M22, matrix.M23, matrix.M24,
                matrix.M31, matrix.M32, matrix.M33, matrix.M34, matrix.M41, matrix.M42, matrix.M43, matrix.M44 },
            projectionMatrix = new[] { projection.M11, projection.M12, projection.M13, projection.M14,
                projection.M21, projection.M22, projection.M23, projection.M24, projection.M31, projection.M32, projection.M33, projection.M34,
                projection.M41, projection.M42, projection.M43, projection.M44 },
            projection = camera.Projection.ToString(), camera.VerticalFieldOfView, camera.NearClipPlane, camera.FarClipPlane,
            hostScheduling = new { game.IsFixedTimeStep, game.TreatNotFocusedLikeMinimized, game.DrawWhileMinimized,
                windowMinimumMilliseconds = game.WindowMinimumUpdateRate.MinimumElapsedTime.TotalMilliseconds,
                minimizedMinimumMilliseconds = game.MinimizedMinimumUpdateRate.MinimumElapsedTime.TotalMilliseconds,
                vSync = game.GraphicsDeviceManager.SynchronizeWithVerticalRetrace },
            cadenceBoundary = "scene authority actual engine update/draw clock; host controls submission scheduling" };
    }
}
