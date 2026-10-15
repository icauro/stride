using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stride.Core.Diagnostics;
using Stride.Core.Reflection;
using Stride.Editor.Thumbnails;
using Stride.Graphics;

namespace Stride.AssetCompiler;

// Versioned native resident transport over the real production thumbnail session.
internal static class NativeThumbnailWorkerServer
{
    internal sealed class Command
    {
        public int Version { get; set; } = 1;
        public string Operation { get; set; } = "";
        public string RequestId { get; set; } = "";
        public string RequestPath { get; set; } = "";
        public string RootIdentity { get; set; } = "";
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static int Run(string initializationPath)
    {
        using var diagnostics = EditorAssetDiagnosticLog.Create();
        var initial = Read(initializationPath);
        // The private deployment must contain this exact closure before Main.
        // Additional plugin modules can load only from their certified paths.
        foreach (var certificate in initial.Assemblies)
        {
            if (AssemblyName.GetAssemblyName(certificate.Path).FullName != certificate.Identity
                || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(certificate.Path))) != certificate.Sha256)
                throw new InvalidDataException("Invalid resident thumbnail module certificate.");
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(item => item.FullName == certificate.Identity)
                ?? Assembly.LoadFrom(certificate.Path);
            if (assembly.ManifestModule.ModuleVersionId.ToString("D") != certificate.ModuleVersionId)
                throw new InvalidDataException("A resident thumbnail module differs from its canonical generation.");
            RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);
            AssemblyRegistry.Register(assembly, AssemblyCommonCategories.Assets);
        }
        var ownedDirectory = Path.GetFullPath(Path.GetDirectoryName(initializationPath)!);
        var log = new LoggerResult("Thumbnail");
        log.MessageLogged += (_, args) => Console.WriteLine(args.Message);
        using var session = new NativeThumbnailBatchSession(initial.Generation, initial.Assemblies, initial.Inputs.BuildDirectory, log);
        using var commands = new BlockingCollection<Command>();
        var cancellations = new ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>(StringComparer.Ordinal);
        var cancelledRoots = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        using var shutdown = new CancellationTokenSource();
        using var activeLock = new ReaderWriterLockSlim();
        CancellationTokenSource? activeCancellation = null;
        string? activeRequest = null;
        string[] activeRoots = [];
        var reader = Task.Run(() =>
        {
            try
            {
                while (Console.ReadLine() is { } line)
                {
                    if (line.Length > 16384) throw new InvalidDataException("The thumbnail control command exceeds its bound.");
                    var command = JsonSerializer.Deserialize<Command>(line) ?? throw new InvalidDataException("Invalid thumbnail control command.");
                    if (command.Version != 1) throw new InvalidDataException("Unsupported thumbnail control protocol.");
                    if (command.Operation != "stop" && !Guid.TryParseExact(command.RequestId, "N", out _))
                        throw new InvalidDataException("Invalid thumbnail control request identity.");
                    if (command.Operation == "run") commands.Add(command, shutdown.Token);
                    else if (command.Operation is "cancel-root" or "cancel-batch")
                    {
                        var cancelled = cancellations.GetOrAdd(command.RequestId, _ => new(StringComparer.Ordinal));
                        if (command.Operation == "cancel-root") cancelled[command.RootIdentity] = 0;
                        else cancelled["*batch*"] = 0;
                        activeLock.EnterReadLock();
                        try
                        {
                            if (command.RequestId != activeRequest) continue;
                            if (command.Operation == "cancel-batch" || activeRoots.All(cancelled.ContainsKey)) activeCancellation?.Cancel();
                        }
                        finally { activeLock.ExitReadLock(); }
                    }
                    else if (command.Operation == "stop") break;
                    else throw new InvalidDataException("Unknown thumbnail control command.");
                }
            }
            finally
            {
                shutdown.Cancel();
                activeLock.EnterReadLock();
                try { activeCancellation?.Cancel(); }
                finally { activeLock.ExitReadLock(); }
                commands.CompleteAdding();
            }
        });
        Write("ready", "", initial.Generation);
        foreach (var command in commands.GetConsumingEnumerable())
        {
            if (shutdown.IsCancellationRequested) break;
            var path = Path.GetFullPath(command.RequestPath);
            if (!path.StartsWith(ownedDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The thumbnail batch request lies outside the owned worker directory.");
            var batch = Read(path);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
            activeLock.EnterWriteLock();
            try
            {
                cancelledRoots = cancellations.GetOrAdd(command.RequestId, _ => new(StringComparer.Ordinal)); activeRequest = command.RequestId;
                activeRoots = batch.Roots.Select(root => root.Identity).ToArray(); activeCancellation = cancellation;
                if (cancelledRoots.ContainsKey("*batch*") || activeRoots.All(cancelledRoots.ContainsKey)) cancellation.Cancel();
            }
            finally { activeLock.ExitWriteLock(); }
            try
            {
                var results = session.Execute(batch, cancellation.Token, cancelledRoots.ContainsKey);
                foreach (var result in results) Write("result", command.RequestId, result);
                Write("complete", command.RequestId, batch.Generation);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            { Write("cancelled", command.RequestId, batch.Generation); }
            catch (GraphicsDeviceException error) when (error.Status != GraphicsDeviceStatus.Normal)
            { Write("generation-failure", command.RequestId, error.ToString()); }
            catch (Exception error)
            { Write("failure", command.RequestId, error.ToString()); }
            finally
            {
                activeLock.EnterWriteLock();
                try { activeCancellation = null; activeRequest = null; activeRoots = []; }
                finally { activeLock.ExitWriteLock(); }
                cancellations.TryRemove(command.RequestId, out _);
            }
        }
        reader.GetAwaiter().GetResult();
        return 0;
    }

    private static NativeThumbnailBatchParameters Read(string path)
        => JsonSerializer.Deserialize<NativeThumbnailBatchParameters>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Invalid resident thumbnail batch request.");
    private static void Write(string kind, string requestId, object value)
        => Console.WriteLine("@@stride-thumbnail:" + JsonSerializer.Serialize(new { version = 1, kind, requestId, value }));
}
