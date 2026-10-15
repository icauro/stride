using System.Text.Json.Serialization;
using System.Text.Json;

namespace Stride.GameStudio.Diagnostics.VisualCapture;

internal sealed record CaptureRectangle(int X, int Y, int Width, int Height);
internal sealed record CaptureDpi(double X, double Y);
internal sealed class CaptureTarget
{
    public string Id { get; set; } = "";
    public string InstanceId => Id;
    public string[] SemanticIds { get; set; } = [];
    public string Kind { get; set; } = "element";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string ParentId { get; set; }
    public string AutomationId { get; set; }
    public string FrameworkName { get; set; }
    public string ContentId { get; set; }
    public string State { get; set; } = "unavailable";
    public bool Visible { get; set; }
    public string Hwnd { get; set; } = "0x0";
    public CaptureRectangle Rect { get; set; } = new(0, 0, 0, 0);
    public CaptureRectangle UnclippedRect { get; set; }
    public bool Clipped { get; set; }
    public string CoordinateSpace => "screen-physical";
    public CaptureDpi Dpi { get; set; } = new(96, 96);
    public bool Foreground { get; set; }
    public string BoundsGeneration { get; set; }
    public string NativeChildHwnd { get; set; }
    public string BoundsSource { get; set; } = "none";
    public string Reason { get; set; }
    [JsonIgnore] public object Source { get; set; }
}
internal sealed class CaptureRequest
{
    public int ProtocolVersion { get; set; }
    public string Id { get; set; } = "";
    public string SessionId { get; set; }
    public string Operation { get; set; } = "";
    public string Target { get; set; }
    public string Action { get; set; }
    public JsonElement Parameters { get; set; }
    public string Output { get; set; }
    public bool Focus { get; set; }
    public bool Reveal { get; set; }
    public int SettleFrames { get; set; } = 2;
    public int TimeoutMs { get; set; } = 15000;
    public int MaxDepth { get; set; } = 128;
    public int MaxNodes { get; set; } = 100000;
}
internal sealed class CaptureEndpointException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
