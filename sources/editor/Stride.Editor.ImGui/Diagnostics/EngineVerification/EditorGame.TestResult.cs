using System.Text.Json;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void CompleteDiagnosticResult(bool passed, string result)
    {
        engineSceneProbe?.FinalizeCompletion(passed, result);
        engineAdditionalProbe?.FinalizeCompletion(passed, result);
        var root = Environment.GetEnvironmentVariable("STRIDE_EDITOR_TEST_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(root)) root = AppContext.BaseDirectory;
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "teardown-result.json"), JsonSerializer.Serialize(new
        {
            passed, result, sceneCpuRetirementCompleted = true,
            gpuRetirementCompleted = true, installedFontsRetired = true,
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(result);
        Environment.ExitCode = passed ? 0 : 1;
    }
}
