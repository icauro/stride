using Stride.Core;
using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed record PlatformProjectInput(Guid Id, string ProjectPath, string PackagePath, ProjectType Type, PlatformType Platform, bool IsImplicitProject);
internal sealed record PlatformDependencyInput(string ProjectPath, string Name, string? VersionRange, DependencyType Type, string MSBuildProject);
internal sealed record PlatformAuthoredInput(string Path, byte[] Bytes);
internal sealed record PackagePlatformUpdateSnapshot(string LibraryPath, string Root, string SelectedPackagePath,
    string Name, PlatformProjectInput[] Projects, PlatformAuthoredInput[] Inputs, PlatformDependencyInput[] RemovingDependencies, PlatformType[] Selected, bool Regenerate, Guid[] ExpectedRemovedProjects, PlatformType[] ExpectedInstantiation);
