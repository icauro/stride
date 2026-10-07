using Stride.Engine;

if (args.Contains("--benchmark"))
{
    BenchmarkRunner.Run(args);
    return;
}

using var game = new Game();
game.Run();
