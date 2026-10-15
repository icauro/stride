// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

namespace Stride.Core.Assets;

public class PackageSaveParameters
{
    private static readonly PackageSaveParameters DefaultParameters = new();

    public static PackageSaveParameters Default()
    {
        return DefaultParameters.Clone();
    }

    /// <summary>
    /// Clones this instance.
    /// </summary>
    /// <returns>PackageLoadParameters.</returns>
    public PackageSaveParameters Clone()
    {
        return (PackageSaveParameters)MemberwiseClone();
    }

    public Func<AssetItem, bool>? AssetFilter;

    /// <summary>
    /// Optional filter selecting which packages to save; packages for which it returns false are
    /// skipped without writing (their in-memory dirty state is preserved). Null saves all.
    /// </summary>
    public Func<Package, bool>? PackageFilter;

    /// <summary>
    /// Optional removal policy for old asset paths, queued obsolete package files
    /// and generated project artifacts. When supplied, it entirely owns removal;
    /// saving never follows it with a pathname delete. Null preserves default behavior.
    /// </summary>
    public Action<Package, string, PackageFileRemovalKind>? FileRemoval;

    /// <summary>
    /// Optional canonical project membership policy. True means the owner handled
    /// this addition/removal and core must not load or rewrite its project XML.
    /// False retains the ordinary project save behavior for that asset.
    /// </summary>
    public Func<SolutionProject, AssetItem, bool, bool>? ProjectItemChanges;

    /// <summary>Optional canonical solution writer. Null uses the native solution save.</summary>
    public Action<Stride.Core.Solutions.Solution>? SolutionSave;

    /// <summary>
    /// Optional owner of serialized package metadata publication. Native relative-path,
    /// authored-name and implicit-project policies still run; null retains native file saving.
    /// </summary>
    public Action<Package, byte[]>? PackageWrite;
}
