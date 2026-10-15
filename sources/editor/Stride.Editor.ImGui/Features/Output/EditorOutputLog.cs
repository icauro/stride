using Stride.Core.Diagnostics;
using Stride.Core.Assets;
using Stride.Core.Assets.Diagnostics;

namespace Stride.Editor.ImGui;

/// <summary>Thread-safe channel histories of complete engine/editor messages, retained until Clear.</summary>
internal sealed class EditorOutputLog : IDisposable
{
    private readonly object gate = new();
    private readonly Queue<EditorOutputEntry> messages = new();
    private readonly Dictionary<Guid, CompilerRunIdentity> activeCompilerRuns = [];
    private readonly Dictionary<string, CompilerRunIdentity> latestCompilerRuns = new(StringComparer.Ordinal);
    private long version;
    private long messageVersion, capturedMessageVersion = -1, nextMessageSequence;
    private EditorOutputEntry[] capturedMessages = [];
    private bool disposed;
    public EditorOutputLog() => GlobalLogger.GlobalMessageLogged += OnMessage;
    private void OnMessage(ILogMessage message)
    {
        var id = message is AssetLogMessage typed ? typed.AssetReference?.Id
            : message is AssetSerializableLogMessage serialized ? serialized.AssetId : (AssetId?)null;
        var url = message is AssetLogMessage located ? located.AssetReference?.Location
            : message is AssetSerializableLogMessage serializedLocation ? serializedLocation.AssetUrl?.ToString() : null;
        Append("Project", message.Type, message.Module, message.Text, message.ExceptionInfo?.ToString(), assetId: id, assetUrl: url,
            exceptionType: message.ExceptionInfo?.TypeFullName);
    }
    public void Append(string channel, LogMessageType level, string? module, string text, string? exception = null, BuildDiagnostic? diagnostic = null, AssetId? assetId = null, CompilerRunIdentity? compilerRun = null, string? assetUrl = null, string? exceptionType = null)
    {
        // The legacy Live-scripting tab is collapsed in the reference shell.
        // Keep the port's active live-session diagnostics in its visible Project log.
        if (channel == "Live scripting") channel = "Project";
        var full = string.IsNullOrEmpty(exception) ? text : text + Environment.NewLine + exception;
        lock (gate)
        {
            if (disposed) return;
            messages.Enqueue(new EditorOutputEntry(channel, level, module, full, diagnostic, exception, assetId, text, compilerRun,
                assetUrl, exceptionType, ++nextMessageSequence));
            messageVersion++; version++;
        }
    }
    internal void RecordCompilerLifecycle(CompilerRunIdentity run, CompilerRunEvent state)
    {
        lock (gate)
        {
            if (disposed) return;
            if (state == CompilerRunEvent.Started) activeCompilerRuns[run.Id] = run;
            else if (state != CompilerRunEvent.None)
            {
                activeCompilerRuns.Remove(run.Id);
                if ((state is CompilerRunEvent.Succeeded or CompilerRunEvent.Failed) && (!latestCompilerRuns.TryGetValue(run.Source, out var previous) || run.Sequence > previous.Sequence))
                    latestCompilerRuns[run.Source] = run;
            }
            version++;
        }
    }
    internal bool ReadCurrentAssetDiagnostics(ref long observedVersion, out EditorOutputEntry[] entries)
    {
        lock (gate)
        {
            if (version == observedVersion) { entries = []; return false; }
            observedVersion = version;
            entries = CaptureMessages().Where(entry => entry.CompilerRun is not { } run || activeCompilerRuns.ContainsKey(run.Id)
                || latestCompilerRuns.TryGetValue(run.Source, out var latest) && latest.Id == run.Id).ToArray();
            return true;
        }
    }
    public bool Read(ref long observedVersion, out EditorOutputEntry[] entries)
    {
        lock (gate)
        {
            if (messageVersion == observedVersion) { entries = []; return false; }
            observedVersion = messageVersion; entries = CaptureMessages(); return true;
        }
    }
    private EditorOutputEntry[] CaptureMessages()
    {
        if (capturedMessageVersion == messageVersion) return capturedMessages;
        var snapshot = messages.ToArray();
        capturedMessages = snapshot; capturedMessageVersion = messageVersion;
        return snapshot;
    }
    public void Clear(string channel)
    {
        lock (gate)
        {
            var retained = messages.Where(message => message.Channel != channel).ToArray();
            messages.Clear();
            foreach (var entry in retained) messages.Enqueue(entry);
            capturedMessages = [];
            messageVersion++; version++;
        }
    }
    public void Dispose()
    {
        GlobalLogger.GlobalMessageLogged -= OnMessage;
        lock (gate) { disposed = true; messages.Clear(); capturedMessages = []; activeCompilerRuns.Clear(); latestCompilerRuns.Clear(); }
    }
}
internal sealed record EditorOutputEntry(string Channel, LogMessageType Level, string? Module, string Text, BuildDiagnostic? Diagnostic = null,
    string? Exception = null, AssetId? AssetId = null, string? MessageText = null, CompilerRunIdentity? CompilerRun = null, string? AssetUrl = null, string? ExceptionType = null,
    long Sequence = 0)
{
    internal string DisplayPrefix => (Module != null ? "[" + Module + "]: " : "") + Level + ": ";
    internal string DisplayMessage(bool showStacktrace)
    {
        var message = MessageText ?? Text;
        if (!string.IsNullOrEmpty(Exception))
            message += showStacktrace ? Environment.NewLine + Exception + Environment.NewLine : " (...)";
        return message;
    }
    internal string DisplayText(bool showStacktrace) => DisplayPrefix + DisplayMessage(showStacktrace) + Environment.NewLine;
}
