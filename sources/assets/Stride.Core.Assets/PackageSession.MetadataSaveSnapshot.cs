using Stride.Core.Diagnostics;
using Stride.Core.Solutions;

namespace Stride.Core.Assets;

public sealed partial class PackageSession
{
    /// <summary>Captures an actor-owned native metadata save input without retaining mutable asset roots or loaded assembly ownership.</summary>
    public PackageMetadataSaveSnapshot CaptureMetadataSaveSnapshot()
    {
        var captured = new PackageSession();
        var owner = new PackageMetadataSaveSnapshot(captured, this);
        try
        {
            captured.VSSolution = VSSolution.Clone();
            var copies = new Dictionary<Package, Package>();
            Package Copy(Package source)
            {
                if (copies.TryGetValue(source, out var existing)) return existing;
                var copy = source.Clone();
                // Package.Clone deliberately shares roots for ordinary session
                // baselines. Native PackageAnalysis must never see those roots here.
                copy.Assets.Clear();
                foreach (var item in source.Assets) copy.Assets.Add(item.Clone(false));
                copy.State = source.State; copy.AuthoredName = source.AuthoredName;
                copy.PreviousPackagePath = source.PreviousPackagePath;
                copy.FilesToDelete.Clear(); copy.FilesToDelete.AddRange(source.FilesToDelete);
                copy.Assets.IsDirty = source.Assets.IsDirty; copy.IsDirty = source.IsDirty;
                copies.Add(source, copy); return copy;
            }
            foreach (var container in Projects)
            {
                var package = Copy(container.Package);
                PackageContainer cloned;
                if (container is SolutionProject project)
                {
                    var native = captured.VSSolution.Projects.FindByGuid(project.Id)
                        ?? throw new InvalidOperationException("The native solution project is absent from its save snapshot.");
                    cloned = new SolutionProject(package, native)
                    {
                        Type = project.Type, Platform = project.Platform, ContainsAssetTypes = project.ContainsAssetTypes,
                        IsImplicitProject = project.IsImplicitProject, TrackDirectDependencies = false,
                        TargetPath = project.TargetPath, AssemblyProcessorSerializationHashFile = project.AssemblyProcessorSerializationHashFile,
                    };
                }
                else cloned = new StandalonePackage(package);
                cloned.AssetNamespace = container.AssetNamespace;
                captured.Projects.Add(cloned);
            }
            // Registration can set native dirty flags; the snapshot retains exact
            // original save eligibility rather than treating capture as an edit.
            foreach (var pair in copies)
            { pair.Value.IsDirty = pair.Key.IsDirty; pair.Value.Assets.IsDirty = pair.Key.Assets.IsDirty; }
            captured.packagesCopy.Clear();
            foreach (var pair in packagesCopy)
            {
                var previousOwner = Copy(pair.Key);
                if (previousOwner.Container == null && pair.Key.Container is SolutionProject removedProject)
                {
                    // Removed packages are intentionally absent from the current
                    // collection, but their saved files retain the real old project
                    // path for source/generated membership removal requests.
                    var old = removedProject.VSProject;
                    _ = new SolutionProject(previousOwner, new Stride.Core.Solutions.Project(old.Guid, old.TypeGuid,
                        old.Name, old.FullPath, old.ParentGuid, old.Sections, old.VersionControlProperties, old.PlatformProperties))
                    { Type = removedProject.Type, Platform = removedProject.Platform, IsImplicitProject = removedProject.IsImplicitProject };
                }
                captured.packagesCopy.Add(previousOwner, Copy(pair.Value));
            }
            captured.AssetNamespaceUsings.UnionWith(AssetNamespaceUsings);
            captured.CurrentProject = CurrentProject is { } current
                ? captured.Projects.OfType<SolutionProject>().Single(project => project.Id == current.Id) : null;
            captured.IsDirty = IsDirty;
            // Saved package baselines are distinct Package objects with the same
            // path. Publication must resolve actual current/removed owners only.
            owner.SetSourcePackages(copies.Where(pair => Projects.Any(container => ReferenceEquals(container.Package, pair.Key))
                || packagesCopy.ContainsKey(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value));
            return owner;
        }
        catch (Exception failure)
        {
            try { owner.Dispose(); }
            catch (Exception cleanup) { throw new PackageMetadataSaveSnapshotCaptureException(failure, cleanup, owner); }
            throw;
        }
    }

    public void AcknowledgeMetadataSaveSnapshot(PackageMetadataSaveSnapshot snapshot)
    {
        var next = LocalPackages.ToDictionary(package => package, package => package.Clone());
        snapshot.Acknowledge(this, (source, detached) =>
        {
            source.IsDirty = detached.IsDirty;
            source.Assets.IsDirty = detached.Assets.IsDirty;
            source.FilesToDelete.Clear(); source.FilesToDelete.AddRange(detached.FilesToDelete);
            source.PreviousPackagePath = detached.PreviousPackagePath;
        });
        packagesCopy.Clear(); foreach (var pair in next) packagesCopy.Add(pair.Key, pair.Value);
        IsDirty = snapshot.PreparedSessionDirty;
        VSSolution.StartupProjectGuid = snapshot.PreparedStartupProject;
        DisarmUpgradeBackup();
    }
}

public sealed record PreparedPackageMetadataWrite(string PackagePath, ReadOnlyMemory<byte> Bytes);
public sealed record PreparedPackageFileRemoval(string PackagePath, string FilePath, PackageFileRemovalKind Kind)
{
    public ReadOnlyMemory<byte> RetainedFileHash { get; init; }
}
public sealed record PreparedProjectAssetMembership(string ProjectPath, AssetId AssetId, string AssetPath, bool Adding);

/// <summary>Detached native input for background save preparation. This owns no original document, assembly registration, or file writer.</summary>
public sealed class PackageMetadataSaveSnapshot : IDisposable
{
    private readonly PackageSession session;
    private readonly PackageSession sourceSession;
    private readonly PackageContainer[] sourceContainers;
    private IReadOnlyDictionary<Package, Package> sourcePackages = new Dictionary<Package, Package>();
    private PreparedPackageMetadataSave? prepared;
    private readonly Dictionary<PreparedProjectAssetMembership, AssetItem> preparedMembershipAssets = [];
    private int phase;
    internal PackageMetadataSaveSnapshot(PackageSession session, PackageSession source)
    { this.session = session; sourceSession = source; sourceContainers = source.Projects.ToArray(); }
    internal void SetSourcePackages(IReadOnlyDictionary<Package, Package> packages) => sourcePackages = packages;
    internal bool PreparedSessionDirty => session.IsDirty;
    internal Guid? PreparedStartupProject => session.VSSolution.StartupProjectGuid;
    public Package FindSourcePackage(string path) => sourcePackages.Keys.Single(package =>
        string.Equals(package.FullPath.ToOSPath(), path, StringComparison.OrdinalIgnoreCase));
    public SolutionProject FindSourceProject(string path) => sourcePackages.Keys.Select(package => package.Container)
        .OfType<SolutionProject>().Single(project => string.Equals(project.FullPath.ToOSPath(), path, StringComparison.OrdinalIgnoreCase));
    public AssetItem FindPreparedMembershipAsset(PreparedProjectAssetMembership receipt) => preparedMembershipAssets[receipt];
    public void ValidateSourceOwner(PackageSession source)
    {
        if (!ReferenceEquals(source, sourceSession) || !source.Projects.SequenceEqual(sourceContainers))
            throw new InvalidOperationException("The native session membership changed during metadata preparation.");
        foreach (var pair in sourcePackages.Where(pair => source.Packages.Contains(pair.Key)))
            if (pair.Key.FullPath != pair.Value.FullPath || !pair.Key.Assets.Select(item => (item.Id, item.Location)).SequenceEqual(pair.Value.Assets.Select(item => (item.Id, item.Location))))
                throw new InvalidOperationException("The native package changed during metadata preparation.");
    }
    internal void Acknowledge(PackageSession source, Action<Package, Package> apply)
    {
        if (Volatile.Read(ref phase) != 2 || prepared == null) throw new InvalidOperationException("The native metadata preparation has not completed.");
        ValidateSourceOwner(source);
        foreach (var write in prepared.Writes)
        {
            using var file = new FileStream(write.PackagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!System.Security.Cryptography.SHA256.HashData(file).AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(write.Bytes.Span)))
                throw new IOException("The published package metadata changed before its save was acknowledged.");
        }
        foreach (var removal in prepared.Removals)
        {
            if (removal.Kind == PackageFileRemovalKind.RetainedAsset ? !File.Exists(removal.FilePath) || Directory.Exists(removal.FilePath) : File.Exists(removal.FilePath) || Directory.Exists(removal.FilePath))
                throw new IOException("A prepared package removal was not published.");
            if (removal.Kind == PackageFileRemovalKind.RetainedAsset && (removal.RetainedFileHash.IsEmpty
                || !System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(removal.FilePath)).AsSpan().SequenceEqual(removal.RetainedFileHash.Span)))
                throw new IOException("The retained asset file changed during package publication.");
        }
        // Only the actor invokes this after all guarded publication hooks succeed.
        foreach (var write in prepared.Writes)
        {
            var package = FindSourcePackage(write.PackagePath);
            package.RememberSavedMetadata(write.PackagePath, write.Bytes.ToArray());
        }
        foreach (var removal in prepared.Removals.Where(removal => removal.Kind == PackageFileRemovalKind.Obsolete))
            FindSourcePackage(removal.PackagePath).AcknowledgeObsoleteMetadataRemoval(removal.FilePath);
        foreach (var pair in sourcePackages.Where(pair => source.LocalPackages.Contains(pair.Key))) apply(pair.Key, pair.Value);
    }
    public PreparedPackageMetadataSave Prepare(ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (Interlocked.CompareExchange(ref phase, 1, 0) != 0)
            throw new InvalidOperationException("The native save snapshot is not available for preparation.");
        var writes = new List<PreparedPackageMetadataWrite>();
        var removals = new List<PreparedPackageFileRemoval>();
        var membership = new List<PreparedProjectAssetMembership>();
        Solution? solution = null;
        try
        {
            // Every publication hook is mandatory. Native preparation may
            // analyze only detached graphs; it never evaluates or writes projects,
            // deletes files, saves assets, or serializes a solution to disk.
            session.Save(log, new PackageSaveParameters
            {
                AssetFilter = _ => false,
                PackageFilter = package => !package.IsReadOnly && package.State >= PackageState.AssetsReady,
                PackageWrite = (package, bytes) => writes.Add(new(package.FullPath.ToOSPath(), bytes.ToArray())),
                FileRemoval = (package, path, kind) => removals.Add(new(package.FullPath.ToOSPath(), path, kind)
                {
                    RetainedFileHash = kind == PackageFileRemovalKind.RetainedAsset
                        ? System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)) : ReadOnlyMemory<byte>.Empty,
                }),
                ProjectItemChanges = (project, asset, adding) =>
                {
                    var receipt = new PreparedProjectAssetMembership(project.FullPath.ToOSPath(), asset.Id, asset.FullPath.ToOSPath(), adding);
                    membership.Add(receipt); preparedMembershipAssets[receipt] = asset; return true;
                },
                SolutionSave = native => solution = native.Clone(),
            });
            if (log is LoggerResult result && result.HasErrors) throw new InvalidOperationException(result.ToText());
            return prepared = new(writes.AsReadOnly(), removals.AsReadOnly(), membership.AsReadOnly(), solution);
        }
        finally { Volatile.Write(ref phase, 2); }
    }
    public void Dispose()
    {
        while (true)
        {
            var current = Volatile.Read(ref phase);
            if (current == 1) throw new InvalidOperationException("The native save preparation is still reading its snapshot.");
            if (current == 3 || Interlocked.CompareExchange(ref phase, 3, current) == current) break;
        }
        session.Dispose();
    }
}

/// <summary>Retains the actual private owner when capture and its cleanup both fail.</summary>
public sealed class PackageMetadataSaveSnapshotCaptureException : AggregateException
{
    internal PackageMetadataSaveSnapshotCaptureException(Exception failure, Exception cleanup, PackageMetadataSaveSnapshot owner)
        : base("The native save snapshot could not be captured or released.", failure, cleanup) => RetainedSnapshot = owner;
    public PackageMetadataSaveSnapshot RetainedSnapshot { get; }
}

public sealed class PreparedPackageMetadataSave
{
    private readonly Solution? solution;
    internal PreparedPackageMetadataSave(IReadOnlyList<PreparedPackageMetadataWrite> writes,
        IReadOnlyList<PreparedPackageFileRemoval> removals, IReadOnlyList<PreparedProjectAssetMembership> membership, Solution? solution)
    { Writes = writes; Removals = removals; ProjectMembership = membership; this.solution = solution; }
    public IReadOnlyList<PreparedPackageMetadataWrite> Writes { get; }
    public IReadOnlyList<PreparedPackageFileRemoval> Removals { get; }
    public IReadOnlyList<PreparedProjectAssetMembership> ProjectMembership { get; }
    public Solution? CaptureSolution() => solution?.Clone();
}
