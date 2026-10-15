using System.Text.Json;
using System.Text.RegularExpressions;

namespace Stride.Editor.ImGui;

internal static class WorkspaceRecoveryFixtureGuard
{
    internal static string Resolve(string package, string[] args)
    {
        var option = Array.IndexOf(args, "--workspace-recovery-fixture");
        if (option < 0) return UiWorkflowTest.CopyFixture(package);
        var scenario = Array.IndexOf(args, "--engine-capability");
        if (!args.Contains("--ui-test") || option + 1 >= args.Length || scenario < 0 || scenario + 1 >= args.Length
            || args[scenario + 1] is not ("E08.recovery.write" or "E08.recovery.read"))
            throw new InvalidOperationException("Same-path recovery fixtures require the explicit recovery capability host.");
        package = Path.GetFullPath(package);
        var expected = Path.GetFullPath(args[option + 1]);
        var projectRoot = Path.GetDirectoryName(package)!;
        var owned = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Stride-ImGui-UI"));
        if (!string.Equals(package, expected, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(package))
            throw new InvalidOperationException("The shared recovery fixture is not an owned test project.");
        using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(projectRoot, ".workspace-recovery-fixture.json")));
        var data = marker.RootElement;
        var version = data.GetProperty("version").GetInt32();
        var root = version == 2 ? Path.GetDirectoryName(projectRoot)! : projectRoot;
        if (version is not (1 or 2)
            || !string.Equals(Path.GetDirectoryName(root), owned, StringComparison.OrdinalIgnoreCase)
            || !Regex.IsMatch(Path.GetFileName(root), "^recovery-[0-9a-f]{32}$", RegexOptions.CultureInvariant)
            || !ExactPath(data, "packagePath", package) || !ExactPath(data, "ownedRoot", root))
            throw new InvalidDataException("The recovery fixture receipt does not match its canonical project.");
        if (version == 2)
        {
            var externalLinked = data.GetProperty("externalLinkedSource").ValueKind == JsonValueKind.True;
            var protectedSources = data.TryGetProperty("generatedReadOnlySources", out var protectedFlag) && protectedFlag.ValueKind == JsonValueKind.True;
            if (externalLinked == protectedSources || protectedSources != args.Contains("--recovery-generated-readonly-sources"))
                throw new InvalidDataException("Recovery fixture variants require their exact independent capability flag.");
            if (!string.Equals(projectRoot, Path.Combine(root, "Project"), StringComparison.OrdinalIgnoreCase)
                || !ExactPath(data, "projectRoot", projectRoot)
                || !ExactPath(data, "sourceProjectPath", Path.ChangeExtension(package, ".csproj"))
                || !ExactPath(data, "includedSourcePath", Path.Combine(projectRoot, "Scripts", "RecoveryIncluded.cs"))
                || !ExactPath(data, "excludedSourcePath", Path.Combine(projectRoot, "Scripts", "RecoveryExcluded.cs"))
                || !ExactPath(data, "linkedSourcePath", Path.Combine(externalLinked ? root : projectRoot, "LinkedSources", "RecoveryLinked.cs"))
                || data.GetProperty("linkInclude").GetString() != (externalLinked ? "../LinkedSources/RecoveryLinked.cs" : "LinkedSources/RecoveryLinked.cs")
                || data.GetProperty("linkVirtual").GetString() != "Scripts/RecoveryLinked.cs")
                throw new InvalidDataException("The external linked recovery fixture has another project or source boundary.");
            foreach (var property in new[] { "packagePath", "sourceProjectPath", "includedSourcePath", "excludedSourcePath", "linkedSourcePath" })
                RequireOwnedPhysicalPath(data.GetProperty(property).GetString()!, root, owned);
            RequireOwnedPhysicalPath(Path.Combine(projectRoot, ".workspace-recovery-fixture.json"), root, owned);
            if (protectedSources)
            {
                var readOnlyPath = Path.Combine(projectRoot, "Scripts", "RecoveryReadOnly.cs");
                var externalPath = Path.Combine(root, "ExternalViews", "RecoveryExternalReadOnly.cs");
                if (!ExactPath(data, "readOnlySourcePath", readOnlyPath) || !ExactPath(data, "externalReadOnlySourcePath", externalPath))
                    throw new InvalidDataException("Protected recovery source views have another fixture boundary.");
                RequireOwnedPhysicalPath(readOnlyPath, root, owned);
                RequireOwnedPhysicalPath(externalPath, root, owned);
                if ((File.GetAttributes(readOnlyPath) & FileAttributes.ReadOnly) == 0
                    || (File.GetAttributes(externalPath) & FileAttributes.ReadOnly) != 0)
                    throw new InvalidDataException("Protected recovery source fixture file permissions do not match the declared presentations.");
            }
        }
        return package;
    }

    private static bool ExactPath(JsonElement data, string name, string expected)
    {
        var path = data.GetProperty(name).GetString();
        return path != null && Path.IsPathFullyQualified(path)
            && string.Equals(path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)
            && string.Equals(path, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static void RequireOwnedPhysicalPath(string path, string root, string owned)
    {
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new InvalidDataException("External linked source must exist inside its declared owned fixture container.");
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Recovery fixtures cannot escape their physical ownership through directory links.");
            if (string.Equals(current, owned, StringComparison.OrdinalIgnoreCase)) return;
        }
        throw new InvalidDataException("Recovery fixture physical ownership chain is incomplete.");
    }
}
