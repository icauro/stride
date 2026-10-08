using Stride.Engine;

if (args.Contains("--benchmark"))
{
    BenchmarkRunner.Run(args);
    return;
}

// --scene <url> starts in another scene than the Game Settings default, e.g. --scene scene/HeadShowcase.
var sceneIndex = Array.IndexOf(args, "--scene");
// --verify-head <folder> checks the GPU-morphed head against Blender's reference shapes, see HeadVerification.
var verifyIndex = Array.IndexOf(args, "--verify-head");
var verifyFolder = verifyIndex >= 0 && verifyIndex + 1 < args.Length ? args[verifyIndex + 1] : null;
using var game = new SampleGame(verifyFolder != null ? "scene/HeadShowcase" : sceneIndex >= 0 && sceneIndex + 1 < args.Length ? args[sceneIndex + 1] : null, verifyFolder);
game.Run();

sealed class SampleGame(string initialScene, string verifyHeadFolder = null) : Game
{
    protected override void Initialize()
    {
        base.Initialize();
        if (initialScene != null)
            SceneSystem.InitialSceneUrl = initialScene;
        if (verifyHeadFolder != null)
            HeadVerification.Start(this, verifyHeadFolder);
    }

    private bool captureRequested;
    private Stride.Core.Mathematics.Color[] captured;

    /// <summary>Reads back the next frame before it is presented; a flip swap chain discards it after.</summary>
    public async Task<Stride.Core.Mathematics.Color[]> CaptureFrame()
    {
        captured = null;
        captureRequested = true;
        while (captured == null)
            await Script.NextFrame();
        return captured;
    }

    protected override void Draw(Stride.Games.GameTime gameTime)
    {
        base.Draw(gameTime);
        if (captureRequested)
        {
            captureRequested = false;
            captured = GraphicsDevice.Presenter.BackBuffer.GetData<Stride.Core.Mathematics.Color>(GraphicsContext.CommandList);
        }
    }
}
