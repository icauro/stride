using System.Reflection;
using System.Security.Cryptography;
using Stride.Core.Assets;
using Stride.Core.Diagnostics;
using Stride.Core.Reflection;
using Stride.Core.Serialization;
using Stride.Core.Settings;
using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

/// <summary>Actual package assembly versions and their resolver/serializer registration, retained for shared replay.</summary>
internal sealed class ScriptAssemblyReloadOperation : Operation
{
    private sealed class Version(Assembly assembly, string path)
    {
        public Assembly Assembly { get; } = assembly;
        public string Path { get; } = path;
        public LoadedAssembly? ContainerEntry;
        public IReadOnlyCollection<string> Categories = [];
        public IDisposable? ArtifactLease;
        public SettingsKeyGeneration? SettingsKeys;
        public PackageSettingsRegistry.ProviderGeneration? SettingsProviders;
    }
    private sealed record Owner(PackageLoadedAssembly Item, string BeforePath, string NextPath, byte[] NextHash);
    private sealed class Replacement(KeyValuePair<PackageLoadedAssembly, string>[] owners)
    {
        public Owner[] Owners { get; } = owners.Select(pair => new Owner(pair.Key, pair.Key.Path, Path.GetFullPath(pair.Value), SHA256.HashData(File.ReadAllBytes(pair.Value)))).ToArray();
        public Version? Before { get; } = owners[0].Key.Assembly is { } assembly ? new(assembly, owners[0].Key.Path) : null;
        public Version? After;
        public string NextPath => Owners[0].NextPath;
    }
    private readonly AssemblyContainer container;
    private Replacement[] replacements;
    private readonly ILogger log;
    private readonly IAssemblyArtifactLifetime? artifacts;
    private bool applied;
    private readonly List<Action> pendingRecovery = [];
    internal ScriptAssemblyReloadOperation(AssemblyContainer container, IReadOnlyDictionary<PackageLoadedAssembly, string> next, ILogger log, IAssemblyArtifactLifetime? artifacts = null)
    {
        this.container = container; this.log = log; this.artifacts = artifacts;
        replacements = next.GroupBy(pair => (object?)pair.Key.Assembly ?? Path.GetFullPath(pair.Value).ToUpperInvariant()).Select(group => new Replacement(group.ToArray())).ToArray();
        if (replacements.Length == 0) throw new InvalidOperationException("Assembly reload requires actual package owners.");
        foreach (var replacement in replacements)
        {
            if (replacement.Owners.Any(owner => !owner.NextHash.AsSpan().SequenceEqual(replacement.Owners[0].NextHash)))
                throw new InvalidOperationException("Shared package owners have different compiled assembly outputs.");
            if (replacement.Before != null && !container.LoadedAssemblies.Any(entry => ReferenceEquals(entry.Assembly, replacement.Before.Assembly)))
                throw new InvalidOperationException("The assembly is not owned by this package session's resolver.");
            var identity = AssemblyName.GetAssemblyName(replacement.NextPath);
            if (replacement.Before != null && !AssemblyName.ReferenceMatchesDefinition(replacement.Before.Assembly.GetName(), identity))
                throw new InvalidOperationException("The replacement assembly does not match its actual package identity.");
        }
        foreach (var replacement in replacements)
            if (replacement.Before is { } before)
            {
                before.ContainerEntry = container.LoadedAssemblies.Single(entry => ReferenceEquals(entry.Assembly, before.Assembly));
                before.ArtifactLease = before.ContainerEntry.AcquireArtifactLifetime();
            }
    }
    internal IReadOnlyCollection<Assembly> OriginalAssemblies => replacements.Where(change => change.Before != null).Select(change => change.Before!.Assembly).ToArray();
    internal void ApplyInitial()
    {
        var files = new List<FileStream>();
        try
        {
            foreach (var replacement in replacements)
            foreach (var owner in replacement.Owners)
            {
                var file = new FileStream(owner.NextPath, FileMode.Open, FileAccess.Read, FileShare.Read); files.Add(file);
                if (!ReferenceEquals(owner.Item.Assembly, replacement.Before?.Assembly) || owner.Item.Path != owner.BeforePath
                    || !SHA256.HashData(file).AsSpan().SequenceEqual(owner.NextHash))
                    throw new InvalidOperationException("The assembly reload snapshot changed.");
            }
            Switch(true, true); applied = true;
        }
        finally { foreach (var file in files) file.Dispose(); }
    }
    internal void CancelInitial() { Recover(); if (applied) { Switch(false, false); applied = false; } ReleaseVersionArtifacts(); }
    protected override void Rollback() { Recover(); if (applied) { Switch(false, false); applied = false; } }
    protected override void Rollforward() { Recover(); Switch(true, false); applied = true; }
    protected override void FreezeContent()
    {
        if (pendingRecovery.Count != 0) throw new InvalidOperationException("Recover the retained actual assembly owners before discarding this operation.");
        ReleaseVersionArtifacts();
        replacements = [];
    }
    private void ReleaseVersionArtifacts()
    {
        foreach (var version in replacements.SelectMany(replacement => new[] { replacement.Before, replacement.After }).Where(version => version != null))
        { version!.ArtifactLease?.Dispose(); version.ArtifactLease = null; }
    }
    private void Recover()
    {
        while (pendingRecovery.Count != 0) { pendingRecovery[0](); pendingRecovery.RemoveAt(0); }
    }

    private void Switch(bool forward, bool first)
    {
        var removed = new List<(Replacement Change, Version? Version)>();
        var restored = new List<(Replacement Change, Version? Version)>();
        try
        {
            foreach (var replacement in replacements.Reverse())
            {
                var current = forward ? replacement.Before : replacement.After ?? throw new InvalidOperationException("The replacement version is not retained.");
                if (replacement.Owners.Any(owner => !ReferenceEquals(owner.Item.Assembly, current?.Assembly))) throw new InvalidOperationException("A shared package assembly owner changed.");
                removed.Add((replacement, current)); Detach(replacement, current);
            }
            foreach (var replacement in replacements)
            {
                if (first)
                {
                    var name = AssemblyName.GetAssemblyName(replacement.NextPath).Name!;
                    using var keys = SettingsContainer.CaptureAssemblyKeys(name);
                    using var providers = PackageSettingsRegistry.CaptureAssemblyProviders(name);
                    var assembly = container.LoadAssemblyFromPath(replacement.NextPath, log, artifacts);
                    // The native loader can retain its resolver entry even when a module initializer fails.
                    var entry = container.LoadedAssemblies.SingleOrDefault(candidate => string.Equals(Path.GetFullPath(candidate.Path), replacement.NextPath, StringComparison.OrdinalIgnoreCase));
                    if (assembly == null)
                    {
                        if (entry != null)
                        {
                            replacement.After = new(entry.Assembly, replacement.NextPath) { ContainerEntry = entry };
                            restored.Add((replacement, replacement.After));
                        }
                        throw new InvalidOperationException("The replacement assembly could not be initialized.");
                    }
                    replacement.After = new(assembly, replacement.NextPath)
                    { ContainerEntry = entry ?? throw new InvalidOperationException("The loaded assembly has no actual resolver owner."),
                        Categories = AssemblyRegistry.GetCategories(assembly).Append(AssemblyCommonCategories.Assets).Distinct().ToArray() };
                    restored.Add((replacement, replacement.After));
                    replacement.After.SettingsKeys = keys.Complete(assembly);
                    replacement.After.SettingsProviders = providers.Complete(assembly);
                    replacement.After.ArtifactLease = replacement.After.ContainerEntry.AcquireArtifactLifetime();
                }
                var target = forward ? replacement.After! : replacement.Before;
                if (!first) restored.Add((replacement, target));
                Attach(replacement, target, forward);
            }
        }
        catch (Exception failure)
        {
            var errors = new List<Exception>();
            foreach (var item in restored.AsEnumerable().Reverse()) pendingRecovery.Add(() => Detach(item.Change, item.Version));
            foreach (var item in removed.AsEnumerable().Reverse()) pendingRecovery.Add(() => Attach(item.Change, item.Version, !forward));
            try { Recover(); } catch (Exception error) { errors.Add(error); }
            if (errors.Count != 0) { errors.Insert(0, failure); throw new AggregateException("The assembly versions could not be restored.", errors); }
            throw;
        }
    }
    private void Detach(Replacement change, Version? version)
    {
        if (version == null) return;
        var categories = AssemblyRegistry.GetCategories(version.Assembly).ToArray();
        if (categories.Length != 0) version.Categories = categories;
        try
        {
            if (version.SettingsKeys == null) version.SettingsKeys = SettingsContainer.SuspendAssemblyKeys(version.Assembly);
            else version.SettingsKeys.Suspend();
            if (version.SettingsProviders == null) version.SettingsProviders = PackageSettingsRegistry.SuspendAssemblyProviders(version.Assembly);
            else version.SettingsProviders.Suspend();
            AssemblyRegistry.Unregister(version.Assembly);
            DataSerializerFactory.UnregisterSerializationAssembly(version.Assembly);
            version.ContainerEntry = container.RemoveAssembly(version.Assembly) ?? version.ContainerEntry;
            foreach (var owner in change.Owners) if (ReferenceEquals(owner.Item.Assembly, version.Assembly)) owner.Item.Assembly = null!;
        }
        catch
        {
            if (version.ContainerEntry != null) container.RestoreAssembly(version.ContainerEntry);
            AssemblyRegistry.Register(version.Assembly, version.Categories);
            DataSerializerFactory.RegisterSerializationAssembly(version.Assembly);
            version.SettingsKeys?.Restore(); version.SettingsProviders?.Restore();
            throw;
        }
    }

    private void Attach(Replacement change, Version? version, bool forward)
    {
        if (change.Owners.Any(owner => owner.Item.Assembly != null && !ReferenceEquals(owner.Item.Assembly, version?.Assembly)))
            throw new InvalidOperationException("The package assembly owner changed during recovery.");
        if (version != null)
        {
        if (version.ContainerEntry != null && !container.LoadedAssemblies.Any(entry => ReferenceEquals(entry.Assembly, version.Assembly))) container.RestoreAssembly(version.ContainerEntry);
        AssemblyRegistry.Register(version.Assembly, version.Categories.Count > 0 ? version.Categories : [AssemblyCommonCategories.Assets]);
        DataSerializerFactory.RegisterSerializationAssembly(version.Assembly);
        version.SettingsKeys?.Restore();
        version.SettingsKeys?.RebindCurrent();
        version.SettingsProviders?.Restore();
        }
        foreach (var owner in change.Owners) { owner.Item.Path = forward ? owner.NextPath : owner.BeforePath; owner.Item.Assembly = version?.Assembly!; }
    }
}
