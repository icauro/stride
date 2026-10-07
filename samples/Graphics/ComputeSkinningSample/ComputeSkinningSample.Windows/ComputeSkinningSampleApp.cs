using Stride.Engine;

if (args.Contains("--benchmark"))
{
    BenchmarkRunner.Run(args);
    return;
}

// --scene <url> starts in another scene than the Game Settings default, e.g. --scene scene/CylinderBenchmark.
var sceneIndex = Array.IndexOf(args, "--scene");
using var game = new SampleGame(sceneIndex >= 0 && sceneIndex + 1 < args.Length ? args[sceneIndex + 1] : null);
game.Run();

sealed class SampleGame(string initialScene) : Game
{
    protected override void Initialize()
    {
        base.Initialize();
        if (initialScene != null)
            SceneSystem.InitialSceneUrl = initialScene;
    }
}
