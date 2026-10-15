// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.IO;

namespace Stride.Assets.Templates;

/// <summary>The native Update platforms selection, before file or session mutations.</summary>
public sealed class PlatformUpdatePlan
{
    public Package GameLibrary { get; }
    public string BaseName { get; }
    public UDirectory SessionDirectory { get; }
    public IReadOnlyList<PlatformType> SelectedPlatforms { get; }
    public IReadOnlyList<PlatformType> PlatformsToInstantiate { get; }
    public IReadOnlyList<PlatformProjectRemoval> ProjectsToRemove { get; }
    private readonly PackageSession session;
    private readonly PackageContainer[] projects;
    private readonly string libraryPath, libraryName;
    private readonly (PackageContainer Project, string Path, ProjectType Type, PlatformType Platform)[] projectIdentities;
    internal PlatformUpdatePlan(Package library, string baseName, UDirectory directory, PlatformType[] selected,
        PlatformType[] instantiate, PlatformProjectRemoval[] remove)
    {
        GameLibrary = library; BaseName = baseName; SessionDirectory = directory;
        SelectedPlatforms = Array.AsReadOnly(selected); PlatformsToInstantiate = Array.AsReadOnly(instantiate);
        ProjectsToRemove = Array.AsReadOnly(remove);
        session = library.Session; projects = session.Projects.ToArray();
        libraryPath = library.FullPath.ToOSPath(); libraryName = library.Meta.Name;
        projectIdentities = projects.OfType<SolutionProject>().Select(project =>
            ((PackageContainer)project, project.FullPath.ToOSPath(), project.Type, project.Platform)).ToArray();
    }
    public bool IsCurrent(Package library)
        => ReferenceEquals(library, GameLibrary) && ReferenceEquals(library.Session, session)
            && library.FullPath.ToOSPath() == libraryPath && library.Meta.Name == libraryName
            && session.Projects.SequenceEqual(projects)
            && projectIdentities.All(identity => identity.Project is SolutionProject project
                && project.FullPath.ToOSPath() == identity.Path && project.Type == identity.Type && project.Platform == identity.Platform);
}

/// <summary>The actual native project and directory selected by Update platforms.</summary>
public sealed record PlatformProjectRemoval(PlatformType Platform, SolutionProject? Project, UDirectory Directory);
