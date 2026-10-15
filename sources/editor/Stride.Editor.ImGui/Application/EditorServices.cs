using Stride.Core.Diagnostics;

namespace Stride.Editor.ImGui;

/// <summary>Application services shared by panels; authored history stays with documents.</summary>
internal sealed class EditorServices(Action<string> reportStatus)
{
    internal EditorOutputLog Output { get; } = new();
    private EditorCommandDispatcher? commands;
    internal EditorCommandDispatcher Commands => commands ??= new(Output, reportStatus);
    private EditorPropertyDocumentation? propertyDocumentation;
    internal EditorPropertyDocumentation GetPropertyDocumentation(Func<System.Reflection.Assembly, string?> resolveLocation)
        => propertyDocumentation ??= new(resolveLocation);
}

internal sealed class EditorCommandDispatcher(EditorOutputLog output, Action<string> reportStatus)
{
    internal Action<Exception>? DeviceFailure { get; set; }
    private long commandFailures;
    internal bool TryExecute(Action command)
    {
        var before = Interlocked.Read(ref commandFailures);
        Execute(command);
        return Interlocked.Read(ref commandFailures) == before;
    }
    internal void Execute(Action command)
    {
        try { command(); }
        catch (Exception error)
        {
            Interlocked.Increment(ref commandFailures);
            // A native failure propagates only after all command/widget scopes
            // unwind; the existing failed-tick observer owns recovery.
            DeviceFailure?.Invoke(error);
            reportStatus(error.Message);
            output.Append("Project", LogMessageType.Error, "Editor", error.ToString());
            Console.Error.WriteLine(error);
        }
    }
}
