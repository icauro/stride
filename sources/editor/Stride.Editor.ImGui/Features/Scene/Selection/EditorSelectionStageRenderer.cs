using System;
using Stride.Rendering.Compositing;

namespace Stride.Rendering;

/// <summary>Submits the selection stage only while the host has selected geometry.</summary>
public sealed class EditorSelectionStageRenderer : SingleStageRenderer
{
    public Func<bool> IsEnabled { get; set; }
    public int LastDrawCalls { get; private set; }
    protected override void CollectCore(RenderContext context)
    {
        LastDrawCalls = 0;
        if (IsEnabled?.Invoke() == false) return;
        base.CollectCore(context);
    }
    protected override void DrawCore(RenderContext context, RenderDrawContext drawContext)
    {
        if (IsEnabled?.Invoke() == false) return;
        var before = drawContext.GraphicsDevice.FrameDrawCalls;
        base.DrawCore(context, drawContext);
        LastDrawCalls = checked((int)(drawContext.GraphicsDevice.FrameDrawCalls - before));
    }
}
