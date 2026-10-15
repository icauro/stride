using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using Stride.Assets;
using Stride.Assets.Effect;
using Stride.Core.Assets;
using Stride.Core.Assets.Compiler;
using Stride.Core.BuildEngine;
using Stride.Core.Diagnostics;
using Stride.Core.IO;
using Stride.Editor.Preview;
using Stride.Graphics;
using Stride.Shaders.Compiler;

namespace Stride.Editor.Thumbnails;

/// <summary>
/// Resident production execution. One process owns one certified module/backend
/// generation and drains actual command users before disposal.
/// </summary>
public sealed class NativeThumbnailBatchSession : IDisposable
{
    private readonly object execution = new();
    private readonly string generation;
    private readonly Builder builder;
    private readonly LoggerResult log;
    private readonly AssetCompilerRegistry registry = new();
    private readonly string assemblyIdentity;
    private EffectCompilerBase? effects;
    private ThumbnailGenerator? generator;
    private volatile bool closing;
    private bool disposed;

    public NativeThumbnailBatchSession(string generation, NativeThumbnailAssemblyInput[] assemblies,
        string buildDirectory, LoggerResult log)
    {
        if (string.IsNullOrEmpty(generation)) throw new ArgumentException("A certified worker generation is required.");
        ValidateModules(assemblies);
        this.generation = generation; this.log = log;
        assemblyIdentity = ModuleIdentity(assemblies);
        Stride.Assets.Thumbnails.AssetThumbnailRules.EnsureRegistered();
        Directory.CreateDirectory(buildDirectory);
        const string index = "index.native-thumbnail-batch";
        Builder.OpenObjectDatabase(buildDirectory, index);
        builder = new Builder(log, buildDirectory, index) { ThreadCount = 1 };
    }

    public IReadOnlyList<NativeThumbnailBatchResult> Execute(NativeThumbnailBatchParameters batch,
        CancellationToken cancellation, Func<string, bool>? isRootCancelled = null)
    {
        lock (execution)
        {
            if (closing) throw new ObjectDisposedException(nameof(NativeThumbnailBatchSession));
            cancellation.ThrowIfCancellationRequested();
            if (batch.Generation != generation || ModuleIdentity(batch.Assemblies) != assemblyIdentity)
                throw new InvalidDataException("The batch belongs to a different certified worker generation.");
            ValidateModules(batch.Assemblies); VerifySources(batch);
            if (GraphicsDevice.Platform != batch.Inputs.GraphicsPlatform)
                throw new InvalidDataException("The resident thumbnail graphics API differs from its certified batch.");
            log.Clear(); builder.Reset();
            using var session = Stride.Assets.Media.MediaPreviewPackageSession.Create(batch.Inputs.Assets, batch.Inputs.OutputDirectory, log, input =>
            {
                cancellation.ThrowIfCancellationRequested();
                if (!batch.AssetHashes.TryGetValue(input.Id, out var expected)
                    || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input.Snapshot))) != expected)
                    throw new InvalidDataException("A batch asset snapshot differs from its certificate: " + input.Id);
            });
            var package = session.FindAsset(new AssetId(Guid.Parse(batch.Inputs.RootId)))?.Package
                ?? throw new InvalidDataException("The batch root package is missing.");
            var settings = batch.Inputs.GameSettingsId == null ? GameSettingsFactory.Create()
                : session.FindAsset(new AssetId(Guid.Parse(batch.Inputs.GameSettingsId)))?.Asset as GameSettingsAsset
                    ?? throw new InvalidDataException("The batch game-settings asset is missing.");
            var rendering = settings.GetOrCreate<RenderingSettings>();
            rendering.DefaultGraphicsProfile = batch.Inputs.GraphicsProfile; rendering.ColorSpace = batch.Inputs.ColorSpace;
            settings.GetOrCreate<Stride.Assets.Textures.TextureSettings>().TextureQuality = batch.Inputs.TextureQuality;
            using var stop = cancellation.Register(builder.CancelBuild);
            // Match WPF WaitForShaders: stage actual shader outputs before the
            // GPU effect compiler/generator is first created or reused.
            var shaders = session.Packages.SelectMany(owner => owner.Assets).Where(item => item.Asset is EffectShaderAsset).ToList();
            if (shaders.Count != 0)
            {
                using var shaderContext = new AssetCompilerContext { Package = package, Platform = batch.Inputs.Platform,
                    CompilationContext = typeof(PreviewCompilationContext), BuildConfiguration = "Debug" };
                shaderContext.SetGameSettingsAsset(settings);
                shaderContext.OptionProperties["StrideGraphicsApi"] = batch.Inputs.GraphicsPlatform.ToString();
                var prepared = new AssetDependenciesCompiler(typeof(PreviewCompilationContext)).PrepareMany(shaderContext, shaders);
                foreach (var message in prepared.Messages) log.Log(message);
                if (prepared.HasErrors) return FailAll("The actual shader dependency preparation failed.");
                builder.Root.Add(prepared.BuildSteps);
                if (RunBuilder(cancellation) != BuildResultCode.Successful)
                { cancellation.ThrowIfCancellationRequested(); return FailAll("The actual shader dependency build failed."); }
                MicrothreadLocalDatabases.AddToSharedGroup(builder.Root.OutputObjects); builder.Reset();
            }
            lock (MicrothreadLocalDatabases.OutOfMicrothreadDatabaseLock) MicrothreadLocalDatabases.MountCommonDatabase();
            effects ??= (EffectCompilerBase)EffectCompilerFactory.CreateEffectCompiler(MicrothreadLocalDatabases.GetSharedDatabase());
            generator ??= new ThumbnailGenerator(effects);
            var outcomes = new Dictionary<AssetId, (ThumbnailBuildResult Result, byte[]? Png)>();
            var units = new List<(NativeThumbnailBatchRoot Root, AssetId Id, ListBuildStep Steps)>();
            var list = new ThumbnailListCompiler(generator, (_, completed) =>
            {
                byte[]? bytes = null;
                if (completed.Result == ThumbnailBuildResult.Succeeded && completed.ThumbnailStream != null)
                {
                    using var output = new MemoryStream(); completed.ThumbnailStream.CopyTo(output);
                    if (output.Length > 1024 * 1024) throw new InvalidDataException("A native thumbnail exceeds the bounded PNG limit.");
                    bytes = output.ToArray();
                }
                outcomes[completed.AssetId] = (completed.Result, bytes);
            }, registry);
            foreach (var root in batch.Roots)
            {
                cancellation.ThrowIfCancellationRequested();
                if (isRootCancelled?.Invoke(root.Identity) == true) continue;
                var id = new AssetId(Guid.Parse(root.AssetId));
                var asset = session.FindAsset(id) ?? throw new InvalidDataException("An actual batch root is missing: " + root.AssetId);
                var compiler = registry.GetCompiler(asset.Asset.GetType(), typeof(ThumbnailCompilationContext)) as IThumbnailCompiler
                    ?? throw new InvalidDataException("An actual batch thumbnail compiler is missing.");
                if (compiler.GetType().AssemblyQualifiedName != root.CompilerType)
                    throw new InvalidDataException("The batch factory differs from its certified registration.");
                // Original per-root dependency links and compiler logs stay intact.
                // Builder deduplicates shared command hashes across this union.
                var steps = list.Compile(asset, settings, compiler.IsStatic);
                units.Add((root, id, steps)); builder.Root.Add(steps);
            }
            if (units.Count != 0) RunBuilder(cancellation);
            cancellation.ThrowIfCancellationRequested(); VerifySources(batch);
            if (generator.GraphicsDevice.GraphicsDeviceStatus != GraphicsDeviceStatus.Normal)
                throw GraphicsDeviceException.FromLostDevice(generator.GraphicsDevice, generator.GraphicsDevice.GraphicsDeviceStatus);
            MicrothreadLocalDatabases.AddToSharedGroup(builder.Root.OutputObjects);
            var results = new List<NativeThumbnailBatchResult>();
            foreach (var root in batch.Roots)
            {
                var unit = units.FirstOrDefault(item => item.Root == root);
                if (unit.Steps == null || isRootCancelled?.Invoke(root.Identity) == true)
                { results.Add(new(root.AssetId, root.Identity, ThumbnailBuildResult.Cancelled, LogMessageType.Info, null, null)); continue; }
                var messages = unit.Steps.EnumerateRecursively().SelectMany(step => step.Logger.Messages).ToArray();
                var level = messages.Length == 0 ? LogMessageType.Info : messages.Max(message => message.Type);
                var outcome = outcomes.TryGetValue(unit.Id, out var actual) ? actual : (ThumbnailBuildResult.Failed, (byte[]?)null);
                if (outcome.Item1 != ThumbnailBuildResult.Succeeded || outcome.Item2 == null)
                    level = level < LogMessageType.Error ? LogMessageType.Error : level;
                results.Add(new(root.AssetId, root.Identity, outcome.Item1, level, outcome.Item2,
                    outcome.Item1 == ThumbnailBuildResult.Failed ? messages.FirstOrDefault(message => message.Type >= LogMessageType.Error)?.Text
                        ?? "The actual native thumbnail build failed." : null));
            }
            builder.Reset(); return results;

            IReadOnlyList<NativeThumbnailBatchResult> FailAll(string error)
                => batch.Roots.Select(root => new NativeThumbnailBatchResult(root.AssetId, root.Identity,
                    cancellation.IsCancellationRequested ? ThumbnailBuildResult.Cancelled : ThumbnailBuildResult.Failed,
                    LogMessageType.Error, null, error)).ToArray();
        }
    }

    public void Dispose()
    {
        closing = true; builder.CancelBuild();
        lock (execution)
        {
            if (disposed) return;
            // The synchronous Builder.Run drains all command threads before this
            // lock can be acquired; no provider or GPU owner is disposed in use.
            generator?.StopAsync().GetAwaiter().GetResult(); generator?.Dispose(); generator = null;
            effects?.Dispose(); effects = null; registry.Dispose(); builder.Dispose(); disposed = true;
        }
    }

    private static void VerifySources(NativeThumbnailBatchParameters batch)
    {
        foreach (var source in batch.SourceHashes)
        {
            using var stream = File.OpenRead(source.Key);
            if (Convert.ToHexString(SHA256.HashData(stream)) != source.Value)
                throw new InvalidDataException("An actual batch source changed: " + source.Key);
        }
    }
    private BuildResultCode RunBuilder(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var finished = new CancellationTokenSource();
        // Builder resets its cancellation source inside Run. Retrying cancellation
        // until that synchronous call drains closes the before-IsRunning race;
        // this is cancellation control, never a second build executor.
        var control = System.Threading.Tasks.Task.Run(async () =>
        {
            while (!finished.IsCancellationRequested)
            {
                if (cancellation.IsCancellationRequested) builder.CancelBuild();
                try { await System.Threading.Tasks.Task.Delay(20, finished.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (finished.IsCancellationRequested) { break; }
            }
        });
        try { return builder.Run(Builder.Mode.Build); }
        finally { finished.Cancel(); control.GetAwaiter().GetResult(); }
    }
    private static string ModuleIdentity(IEnumerable<NativeThumbnailAssemblyInput> assemblies)
        => string.Join("\n", assemblies.OrderBy(item => item.Identity, StringComparer.Ordinal)
            .Select(item => item.Identity + "|" + item.ModuleVersionId + "|" + item.Sha256));
    private static void ValidateModules(IEnumerable<NativeThumbnailAssemblyInput> assemblies)
    {
        foreach (var certificate in assemblies)
        {
            var actual = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(assembly => assembly.FullName == certificate.Identity)
                ?? throw new InvalidDataException("A certified resident thumbnail module is not loaded: " + certificate.Identity);
            if (actual.ManifestModule.ModuleVersionId.ToString("D") != certificate.ModuleVersionId
                || string.IsNullOrEmpty(actual.Location)
                || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(actual.Location))) != certificate.Sha256)
                throw new InvalidDataException("A resident thumbnail module differs from its certificate: " + certificate.Identity);
        }
    }
}
