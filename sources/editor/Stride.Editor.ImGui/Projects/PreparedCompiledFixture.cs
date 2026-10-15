using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Stride.Editor.ImGui;

// Opt-in test preparation only. Every hit still owns a fresh project/session,
// native content mount and writable compiler-run copy.
internal static class PreparedCompiledFixture
{
    private sealed record Payload(string Area, string Path, string Hash);
    private sealed record Manifest(int Schema, string Fingerprint, Payload[] Files);
    internal sealed record Key(string Directory, string Fingerprint, string ProjectRoot);
    private static Key? Miss(string reason, object? detail = null) { Trace("miss", reason, detail); return null; }
    private static void Trace(string state, string reason, object? detail = null)
    {
        var directory = Environment.GetEnvironmentVariable("STRIDE_EDITOR_TEST_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(directory)) return;
        try
        {
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "prepared-compiled-cache.jsonl"),
                JsonSerializer.Serialize(new { utc = DateTime.UtcNow, state, reason, detail }) + Environment.NewLine);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    internal static Key? Identify(string project, string compiler, string buildWorkingDirectory)
    {
        var elapsed = Stopwatch.StartNew();
        if (Environment.GetEnvironmentVariable("STRIDE_EDITOR_PREPARED_FIXTURE_REUSE") != "1") return Miss("reuse-disabled");
        var cache = Environment.GetEnvironmentVariable("STRIDE_EDITOR_PREPARED_FIXTURE_CACHE");
        if (string.IsNullOrWhiteSpace(cache)) return Miss("cache-path-missing");
        var arguments = Environment.GetCommandLineArgs();
        var scenarioIndex = Array.IndexOf(arguments, "--ui-scenario");
        if (scenarioIndex >= 0 && scenarioIndex + 1 < arguments.Length
            && (arguments[scenarioIndex + 1].StartsWith("U01", StringComparison.OrdinalIgnoreCase)
                || arguments[scenarioIndex + 1].Equals("U07.errors", StringComparison.OrdinalIgnoreCase))) return Miss("protected-ui-scenario");
        if (arguments.Any(argument => argument.Equals("E04", StringComparison.OrdinalIgnoreCase)
            || argument.Equals("E06.recovery", StringComparison.OrdinalIgnoreCase))) return Miss("protected-engine-scenario");
        try
        {
            var root = Path.GetDirectoryName(Path.GetFullPath(project))!;
            if (!root.Contains(Path.Combine("Stride-ImGui-UI", ""), StringComparison.OrdinalIgnoreCase)) return Miss("not-private-fixture", root);
            var projectXml = XDocument.Load(project);
            // Cross-project/import dependencies need a dependency closure beyond
            // this small fixture; conservative misses use the actual compiler.
            if (projectXml.Descendants().Any(element => element.Name.LocalName is "ProjectReference" or "Import" or "HintPath")) return Miss("external-project-dependency");
            foreach (var reference in projectXml.Descendants().Where(element => element.Name.LocalName == "PackageReference"))
            {
                var version = (string?)reference.Attribute("Version");
                if (string.IsNullOrWhiteSpace(version) || version.IndexOfAny(['*', '[', '(', '$']) >= 0) return Miss("unpinned-package", version);
            }
            foreach (var element in projectXml.Descendants().Where(element => element.Name.LocalName != "PackageReference"))
            {
                foreach (var attribute in element.Attributes().Where(attribute => attribute.Name.LocalName is "Include" or "Update"))
                    if (Path.IsPathRooted(attribute.Value) || attribute.Value.Replace('\\', '/').Split('/').Contains("..")) return Miss("external-project-source", attribute.Value);
            }
            for (var parent = Directory.GetParent(root); parent != null; parent = parent.Parent)
                if (new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props" }
                    .Any(name => File.Exists(Path.Combine(parent.FullName, name)))) return Miss("ancestor-build-rules", parent.FullName);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Add(hash, "prepared-native-v1;Windows;Debug;threads=4;SkipAutoPack=true;SkipPackageUpgrade=true;" + EditorGraphicsBackend.SelectedApi);
            Add(hash, Environment.Version.ToString());
            var configuredSdk = Environment.GetEnvironmentVariable("MSBuildSDKsPath");
            string? configuredRoot = null;
            if (!string.IsNullOrEmpty(configuredSdk))
            {
                configuredSdk = Path.GetFullPath(configuredSdk);
                if (!Directory.Exists(configuredSdk)) return Miss("configured-sdk-missing", configuredSdk);
                configuredRoot = string.Equals(Path.GetFileName(configuredSdk), "Sdks", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetDirectoryName(configuredSdk)! : configuredSdk;
                Trace("inspection", "configured-native-sdk", configuredRoot);
                foreach (var file in Files(configuredRoot).Where(BinaryInput))
                    AddFile(hash, "native-sdk/" + Path.GetRelativePath(configuredRoot, file), file);
            }
            string sdkVersion;
            using (var sdk = Process.Start(new ProcessStartInfo("dotnet", "--version")
            { WorkingDirectory = buildWorkingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true }))
            {
                if (sdk == null) return Miss("sdk-process-missing");
                if (!sdk.WaitForExit(10000)) { sdk.Kill(entireProcessTree: true); return Miss("sdk-version-timeout"); }
                if (sdk.ExitCode != 0) return Miss("sdk-version-failed", sdk.StandardError.ReadToEnd());
                sdkVersion = sdk.StandardOutput.ReadToEnd().Trim(); Add(hash, sdkVersion);
            }
            using (var sdks = Process.Start(new ProcessStartInfo("dotnet", "--list-sdks")
            { WorkingDirectory = buildWorkingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true }))
            {
                if (sdks == null) return Miss("sdk-inventory-missing");
                if (!sdks.WaitForExit(10000)) { sdks.Kill(entireProcessTree: true); return Miss("sdk-inventory-timeout"); }
                if (sdks.ExitCode != 0) return Miss("sdk-inventory-failed", sdks.StandardError.ReadToEnd());
                var inventory = sdks.StandardOutput.ReadToEnd();
                var selected = inventory.Split('\n').FirstOrDefault(line => line.StartsWith(sdkVersion + " [", StringComparison.Ordinal));
                if (selected == null) return Miss("selected-sdk-not-found", new { sdkVersion, inventory });
                var start = selected.IndexOf('[');
                var sdkRoot = Path.Combine(selected[(start + 1)..selected.LastIndexOf(']')], sdkVersion);
                if (!Directory.Exists(sdkRoot)) return Miss("sdk-root-missing", sdkRoot);
                if (!string.Equals(Path.GetFullPath(sdkRoot), configuredRoot, StringComparison.OrdinalIgnoreCase))
                    foreach (var file in Files(sdkRoot).Where(BinaryInput))
                        AddFile(hash, "sdk/" + Path.GetRelativePath(sdkRoot, file), file);
            }
            foreach (var file in Files(root).Where(file => !Generated(root, file)))
            {
                // Absolute/out-of-tree resource references are deliberately not
                // portable. A native cache miss preserves normal load behavior.
                if (Path.GetExtension(file).StartsWith(".sd", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var line in File.ReadLines(file))
                    {
                        var at = line.IndexOf("!file ", StringComparison.Ordinal);
                        if (at < 0) continue;
                        var reference = line[(at + 6)..].Trim().Trim('"', '\'');
                        if (reference.Length == 0) continue;
                        var resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, reference));
                        if (!resolved.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return Miss("external-resource", new { file, reference, resolved, root });
                    }
                }
                AddFile(hash, Path.GetRelativePath(root, file), file);
            }
            HashBinaries(hash, "editor", AppContext.BaseDirectory);
            HashBinaries(hash, "compiler", Path.GetDirectoryName(compiler)!);
            var settings = NuGet.Configuration.Settings.LoadDefaultSettings(root);
            var configs = settings.GetConfigFilePaths().OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
            if (configs.Length == 0) return Miss("nuget-config-missing", root);
            for (var index = 0; index < configs.Length; index++) AddFile(hash, "nuget-config/" + index, configs[index]);
            var packageRoot = NuGet.Configuration.SettingsUtility.GetGlobalPackagesFolder(settings);
            if (string.IsNullOrEmpty(packageRoot)) return Miss("nuget-packages-path-missing", configs);
            packageRoot = Path.GetFullPath(Path.Combine(root, packageRoot));
            if (!Directory.Exists(packageRoot)) return Miss("nuget-packages-directory-missing", packageRoot);
            // Includes the installed transitive package binaries and build rules,
            // so same-version SDK/package replacement invalidates the fixture.
            foreach (var file in Files(packageRoot))
                AddFile(hash, "packages/" + Path.GetRelativePath(packageRoot, file), file);
            var fingerprint = Convert.ToHexString(hash.GetHashAndReset());
            Trace("identified", "inputs-verified", new { fingerprint, root, seconds = elapsed.Elapsed.TotalSeconds });
            return new(Path.Combine(Path.GetFullPath(cache), fingerprint), fingerprint, root);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Xml.XmlException
            or ArgumentException or System.ComponentModel.Win32Exception)
        { return Miss("input-inspection-failed", new { type = error.GetType().Name, error.Message }); }
    }

    internal static bool TryCopy(Key? key, string output, CancellationToken token)
    {
        if (key == null) return false;
        try
        {
            var path = Path.Combine(key.Directory, "manifest.json");
            if (!File.Exists(path)) { Trace("miss", "prepared-manifest-missing", path); return false; }
            var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path));
            if (manifest is not { Schema: 1 } || manifest.Fingerprint != key.Fingerprint
                || !manifest.Files.Any(file => file.Area == "content" && file.Path.Replace('\\', '/') == "db/bundles/default.bundle")) { Trace("miss", "invalid-payload-manifest", path); return false; }
            foreach (var file in manifest.Files)
            {
                token.ThrowIfCancellationRequested();
                if (file.Area is not ("content" or "bin")) { Trace("miss", "invalid-payload-area", file.Area); return false; }
                var source = Contained(Path.Combine(key.Directory, file.Area), file.Path);
                if (!File.Exists(source) || Digest(source) != file.Hash) { Trace("miss", "payload-hash-mismatch", source); return false; }
            }
            foreach (var file in manifest.Files)
            {
                token.ThrowIfCancellationRequested();
                var source = Contained(Path.Combine(key.Directory, file.Area), file.Path);
                var destination = Contained(file.Area == "content" ? output : Path.Combine(key.ProjectRoot, "bin"), file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, true);
            }
            Trace("hit", "verified-private-copy", new { key.Fingerprint, output }); return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { Trace("miss", "payload-copy-failed", new { type = error.GetType().Name, error.Message }); return false; }
    }

    internal static void Publish(Key? key, string output)
    {
        if (key == null || Directory.Exists(key.Directory)) return;
        var staging = key.Directory + "." + Guid.NewGuid().ToString("N");
        try
        {
            var files = new List<Payload>();
            foreach (var (area, root) in new[] { ("content", output), ("bin", Path.Combine(key.ProjectRoot, "bin")) })
            {
                if (!Directory.Exists(root)) continue;
                foreach (var source in Files(root))
                {
                    var relative = Path.GetRelativePath(root, source);
                    var target = Contained(Path.Combine(staging, area), relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(source, target);
                    files.Add(new(area, relative, Digest(target)));
                }
            }
            File.WriteAllText(Path.Combine(staging, "manifest.json"), JsonSerializer.Serialize(new Manifest(1, key.Fingerprint, files.ToArray())));
            Directory.Move(staging, key.Directory); // Complete manifests publish atomically.
            Trace("published", "actual-build-prepared", new { key.Fingerprint, key.Directory });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Trace("publish-failed", "cache-write-failed", error.Message); }
        // Abandoned staging is local preparation output; never delete another
        // process's cache directory or an unvalidated computed tree.
    }

    private static IEnumerable<string> Files(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).OrderBy(file => file, StringComparer.Ordinal);
    private static bool Generated(string root, string file) => Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj" or ".git");
    private static bool BinaryInput(string file) => Path.GetExtension(file).ToLowerInvariant() is ".dll" or ".exe" or ".props" or ".targets" or ".nuspec" or ".json" or ".sdbuild" or ".sdpkg";
    private static void HashBinaries(IncrementalHash hash, string identity, string root)
    { foreach (var file in Directory.EnumerateFiles(root).Where(BinaryInput).OrderBy(file => file, StringComparer.Ordinal)) AddFile(hash, identity + "/" + Path.GetFileName(file), file); }
    private static string Digest(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static void Add(IncrementalHash hash, string value) => hash.AppendData(Encoding.UTF8.GetBytes(value + "\n"));
    private static void AddFile(IncrementalHash hash, string identity, string path) { Add(hash, identity.Replace('\\', '/')); Add(hash, Digest(path)); }
    private static string Contained(string root, string relative)
    {
        root = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Prepared payload escaped its owned root.");
        return path;
    }
}
