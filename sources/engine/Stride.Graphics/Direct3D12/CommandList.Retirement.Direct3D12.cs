#if STRIDE_GRAPHICS_API_DIRECT3D12
namespace Stride.Graphics;

public unsafe partial class CommandList
{
    partial void SubmitRecordedWorkForRetirementPlatform()
    {
        if (currentCommandList.Builder != null) Flush();
    }
}
#endif
