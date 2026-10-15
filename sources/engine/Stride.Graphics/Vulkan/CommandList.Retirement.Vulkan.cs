#if STRIDE_GRAPHICS_API_VULKAN
namespace Stride.Graphics;

public unsafe partial class CommandList
{
    partial void SubmitRecordedWorkForRetirementPlatform()
    {
        if (currentCommandList.Builder != null) Flush();
    }
}
#endif
