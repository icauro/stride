using System.Diagnostics;
using System.Text.Json;

namespace Stride.Editor.ImGui;

/// <summary>Private native service checks without an editor window, GPU, package load or compilation.</summary>
internal static class ParityServiceVerification
{
    internal static int Run(string family, string artifacts)
    {
        if (family is not ("dependencies" or "models" or "progress" or "cross-volume"))
            throw new ArgumentException("Unknown parity service family.", nameof(family));
        var directory = Path.GetFullPath(artifacts);
        if (Directory.Exists(directory) || File.Exists(directory))
            throw new IOException("Service verification requires a new disposable artifacts directory.");
        Directory.CreateDirectory(directory);
        var timer = Stopwatch.StartNew();
        object? evidence = null;
        Exception? failure = null;
        try
        {
            evidence = family switch
            {
                "dependencies" => ProjectParityDependencyVerification.Verify(Path.Combine(directory, "project")),
                "models" => ModelParityCases.RunHistory(Path.Combine(directory, "model-parity-" + Guid.NewGuid().ToString("N"))),
                "cross-volume" => AssetCrossVolumeParityVerification.Verify(directory),
                _ => InspectorParityProgressContracts.Run(),
            };
        }
        catch (Exception error) { failure = error; }
        timer.Stop();
        File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1, scenario = "service." + family, passed = failure == null,
            durationSeconds = timer.Elapsed.TotalSeconds, evidence, error = failure?.ToString(),
            coverage = "Native service/history/persistence checks; no UI, GPU or whole-workspace acceptance.",
        }, new JsonSerializerOptions { WriteIndented = true }));
        if (failure != null) Console.Error.WriteLine(failure);
        return failure == null ? 0 : 1;
    }
}
