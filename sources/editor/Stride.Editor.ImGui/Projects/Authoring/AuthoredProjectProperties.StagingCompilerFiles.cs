using Stride.Core.Assets;
using Microsoft.Build.Evaluation;

namespace Stride.Editor.ImGui;

internal sealed partial class AuthoredProjectProperties
{
    // Actual file-valued inputs of native ResolveKeySource, manifest preparation,
    // and Roslyn Csc. Other task properties require their own ownership contract.
    public static IReadOnlyList<string> NativeCompilerFileProperties { get; } = Array.AsReadOnly(new[]
    {
        "AssemblyOriginatorKeyFile", "KeyOriginatorFile", "ManifestKeyFile", "ApplicationIcon", "ApplicationManifest",
        "Win32Manifest", "Win32Resource", "AppConfig", "AppConfigForCompiler", "SourceLink",
    });

    private static IReadOnlyDictionary<string, string> CaptureNativeCompilerFiles(Project project, string caller)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in NativeCompilerFileProperties)
        {
            var value = project.GetPropertyValue(name);
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (value.Contains("$(", StringComparison.Ordinal) || value.Contains("%(", StringComparison.Ordinal))
                throw new InvalidOperationException("The native compiler resource has unresolved runtime input metadata: " + name);
            files.Add(name, Path.GetFullPath(value, Path.GetDirectoryName(caller)!));
        }
        return files;
    }

    private void PublishNativeCompilerFiles(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> contexts,
        string contextProperty, Func<string, string> mapResource)
    {
        foreach (var context in contexts)
        {
            // Context spelling has already been validated by PublishStagingItems.
            var group = root.CreatePropertyGroupElement(); root.AppendChild(group);
            group.Condition = "'$(" + contextProperty + ")' == '" + context.Key + "'";
            foreach (var file in context.Value)
                group.AddProperty(file.Key, EscapeStagingLiteral(mapResource(file.Value)));
        }
    }
}
