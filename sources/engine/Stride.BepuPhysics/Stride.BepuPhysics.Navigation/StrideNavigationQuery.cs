// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using DotRecast.Core.Numerics;
using DotRecast.Detour;

namespace Stride.BepuPhysics.Navigation;

internal sealed class StrideNavigationQuery(DtNavMesh mesh) : DtNavMeshQuery(mesh)
{
    public DtStatus FindPathWithRaycasts(long startRef, long endRef, RcVec3f start, RcVec3f end,
        IDtQueryFilter filter, Span<long> path, out int count)
    {
        count = 0;
        var status = InitSlicedFindPath(startRef, endRef, start, end, filter, DtFindPathOptions.DT_FINDPATH_ANY_ANGLE);
        if (!status.Succeeded() && !status.InProgress()) return status;

        // Preserve the previous DtFindPathOption's unlimited raycast range.
        // DotRecast 2025.2.1's sliced API otherwise limits it by agent radius.
        m_query.raycastLimitSqr = float.MaxValue;
        while (status.InProgress()) status = UpdateSlicedFindPath(int.MaxValue, out _);
        if (!status.Succeeded()) return status;
        return FinalizeSlicedFindPath(path, out count, path.Length);
    }
}
