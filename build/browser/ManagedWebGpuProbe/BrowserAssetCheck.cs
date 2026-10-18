using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stride.Core.IO;
using Stride.Core.Mathematics;
using Stride.Core.Serialization.Contents;
using Stride.Core.Storage;
using Stride.Engine;
using Stride.Engine.Design;
using Stride.Games;
using Stride.Rendering.Compositing;

internal static class BrowserAssetCheck
{
    public static async Task RunAsync(string baseUrl)
    {
        var lockFile = Path.GetTempFileName();
        using (var stream = File.OpenWrite(lockFile))
        {
            if (NativeLockFile.TryAcquireFile(stream, 0, 1, true) != NativeLockFile.FileLockResult.Unsupported ||
                NativeLockFile.TryLockFile(stream, 0, 1, true)) throw new Exception("Browser reported unsupported OS file locks as acquired.");
            NativeLockFile.TryUnlockFile(stream, 0, 1);
        }
        File.Delete(lockFile);
        var manifest = new Uri(new Uri(baseUrl), "manifest.json");
        using var client = new HttpClient();
        foreach (var mode in new[] { "hash", "missing", "traversal", "version", "duplicate-case", "index-case" })
        {
            using var altered = new HttpClient(new AlterManifest(client, mode));
            try
            {
                await BrowserContent.PreloadAsync(altered, manifest);
                throw new Exception($"Invalid {mode} content unexpectedly installed.");
            }
            catch (InvalidDataException) when (mode != "missing") { }
            catch (HttpRequestException) when (mode == "missing") { }
            var root = VirtualFileSystem.GetAbsolutePath(VirtualFileSystem.ApplicationDatabasePath);
            if (Directory.Exists(root) && Directory.GetFileSystemEntries(root).Length != 0)
                throw new Exception("Failed preload exposed a partial database.");
        }
        try { await BrowserContent.PreloadAsync(client, manifest, maximumBytes: 1); throw new Exception("Oversized content installed."); }
        catch (InvalidDataException) { }
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { await BrowserContent.PreloadAsync(client, manifest, cancellationToken: cancelled.Token); throw new Exception("Cancelled content installed."); }
            catch (OperationCanceledException) { }
        }
        var bundle = await BrowserContent.PreloadAsync(client, manifest);
        using var database = ObjectDatabase.CreateDefaultDatabase(bundle);
        using var provider = new DatabaseFileProvider(database);
        var content = new ContentManager(new DatabaseFileProviderService(provider));
        content.Serializer.LowLevelSerializerSelector = ParameterContainerExtensions.DefaultSceneSerializerSelector;
        var settings = content.Load<GameSettings>(GameSettings.AssetUrl);
        if (settings.PackageName != "BrowserFixture" || settings.DefaultSceneUrl != "/BrowserFixture/MainScene")
            throw new Exception("Browser content GameSettings did not deserialize.");
        var scene = content.Load<Scene>(settings.DefaultSceneUrl);
        if (scene.Entities.Count != 1 || scene.Entities[0].Name != "Browser asset marker" || scene.Entities[0].Transform.Position != new Vector3(1, 2, 3))
            throw new Exception("Browser content scene/entity/transform did not deserialize.");
        var compositor = content.Load<GraphicsCompositor>(settings.DefaultGraphicsCompositorUrl);
        if (!ReferenceEquals(scene, content.Load<Scene>("MainScene"))) throw new Exception("Browser content alias did not resolve to the namespaced scene.");
        content.Unload(scene); // Alias load adds a second reference.
        content.Unload(scene);
        content.Unload(compositor);
        content.Unload(settings);
        try { await BrowserContent.PreloadAsync(client, manifest); throw new Exception("Active database was replaced."); }
        catch (InvalidOperationException) { }
    }

    private sealed class AlterManifest(HttpClient client, string mode) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await client.GetAsync(request.RequestUri, cancellationToken);
            if (request.RequestUri!.AbsolutePath.EndsWith("/manifest.json"))
            {
                var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))!;
                if (mode == "version") json["Version"] = 100;
                else if (mode == "hash") json["Files"]![0]!["Sha256"] = new string('0', 64);
                else if (mode == "duplicate-case")
                {
                    var duplicate = json["Files"]![0]!.DeepClone();
                    duplicate["Path"] = "ALIASES";
                    json["Files"]!.AsArray().Add(duplicate);
                }
                else if (mode == "index-case")
                {
                    foreach (var file in json["Files"]!.AsArray())
                        if (file!["Path"]!.GetValue<string>() == "index") file["Path"] = "Index";
                }
                else json["Files"]![0]!["Path"] = mode == "missing" ? "missing.aliases" : "../escape.aliases";
                response.Content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json");
            }
            return response;
        }
    }
}
