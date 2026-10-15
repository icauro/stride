namespace Stride.Editor.ImGui;

internal sealed partial class SolutionMembershipDocument
{
    internal void Adopt(PreparedSolutionProjects prepared) => GetRemovalMembership().Adopt(prepared);
}
