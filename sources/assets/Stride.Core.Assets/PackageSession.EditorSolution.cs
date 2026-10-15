using System;
using Stride.Core.Solutions;

namespace Stride.Core.Assets;

public sealed partial class PackageSession
{
    /// <summary>The actual solution model used by project registration and serialization.</summary>
    public Solution AuthoringSolution => VSSolution;

    /// <summary>Adopts an actual generated solution model into a fresh, unpublished session.</summary>
    public void AdoptGeneratedSolution(string solutionPath)
    {
        if (Projects.Count != 0 || Packages.Count != 0 || VSSolution.Projects.Count != 0)
            throw new InvalidOperationException("A generated solution can only be adopted by an empty session.");
        VSSolution = Solution.FromFile(solutionPath);
    }

    /// <summary>Serializes this session's actual solution for its final location without publishing it.</summary>
    public void StageGeneratedSolution(string stagedPath, string publicationPath, string generatedRoot, string publishedRoot)
    {
        VSSolution.SaveForLocation(stagedPath, publicationPath, generatedRoot, publishedRoot);
        SolutionPath = publicationPath;
    }
}
