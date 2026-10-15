using System;
using System.Threading.Tasks;
using Stride.Assets.Presentation.AssetEditors.GameEditor.Game;
using Stride.Editor.EditorGame.Game;
using Stride.Core.Mathematics;

namespace Stride.Assets.Presentation.AssetEditors.GameEditor.Services;

public sealed record MatchedBenchmarkGameState(string AssetId, int TargetWidth, int TargetHeight,
    long UpdateFrame, long DrawFrame, double UpdateSeconds, double DrawSeconds, bool Visible,
    bool FixedTimeStep, bool TreatNotFocusedLikeMinimized, bool DrawWhileMinimized,
    double WindowMinimumMilliseconds, double MinimizedMinimumMilliseconds, bool VSync,
    float[] CameraPosition, float[] CameraViewMatrix, float[] CameraProjectionMatrix,
    bool Orthographic, float VerticalFieldOfView, float NearPlane, float FarPlane);

public abstract partial class EditorGameController<TEditorGame> where TEditorGame : EditorServiceGame
{
    // Executed by the real editor-game queue, never guessed from WPF composition ticks.
    public Task<MatchedBenchmarkGameState> CaptureMatchedBenchmarkStateAsync()
    {
        if (!Array.Exists(Environment.GetCommandLineArgs(), argument => argument == "--matched-benchmark"))
            throw new InvalidOperationException("Native benchmark inspection requires --matched-benchmark.");
        return InvokeAsync(() =>
        {
            var camera = Game.EditorServices.Get<IEditorGameCameraService>()
                ?? throw new InvalidOperationException("The actual editor camera service is unavailable.");
            var target = Game.GraphicsDevice.Presenter?.BackBuffer
                ?? throw new InvalidOperationException("The native game presenter is unavailable.");
            return new MatchedBenchmarkGameState(Asset.Id.ToString(), target.Width, target.Height,
                Game.UpdateTime.FrameCount, Game.DrawTime.FrameCount, Game.UpdateTime.Total.TotalSeconds,
                Game.DrawTime.Total.TotalSeconds, !Game.IsEditorHidden, Game.IsFixedTimeStep,
                Game.TreatNotFocusedLikeMinimized, Game.DrawWhileMinimized,
                Game.WindowMinimumUpdateRate.MinimumElapsedTime.TotalMilliseconds,
                Game.MinimizedMinimumUpdateRate.MinimumElapsedTime.TotalMilliseconds,
                Game.GraphicsDeviceManager.SynchronizeWithVerticalRetrace,
                new[] { camera.Position.X, camera.Position.Y, camera.Position.Z }, Values(camera.ViewMatrix), Values(camera.ProjectionMatrix), camera.IsOrthographic,
                camera.VerticalFieldOfView, camera.NearPlane, camera.FarPlane);
        });
    }
    private static float[] Values(Matrix matrix) => new[] { matrix.M11, matrix.M12, matrix.M13, matrix.M14,
        matrix.M21, matrix.M22, matrix.M23, matrix.M24, matrix.M31, matrix.M32, matrix.M33, matrix.M34,
        matrix.M41, matrix.M42, matrix.M43, matrix.M44 };
}
