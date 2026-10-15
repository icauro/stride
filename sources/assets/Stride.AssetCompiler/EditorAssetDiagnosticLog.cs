using System;
using System.IO;
using System.Text.Json;
using Stride.Core.Assets.Diagnostics;
using Stride.Core.Diagnostics;

namespace Stride.AssetCompiler;

/// <summary>Opt-in native compiler diagnostics preserve actual asset identities across the editor process boundary.</summary>
internal sealed class EditorAssetDiagnosticLog : IDisposable
{
    private readonly object gate = new();
    private readonly StreamWriter writer;
    private bool disposed;
    private EditorAssetDiagnosticLog(string path)
    {
        writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read));
        GlobalLogger.GlobalMessageLogged += OnMessage;
    }
    internal static EditorAssetDiagnosticLog? Create()
    {
        var path = Environment.GetEnvironmentVariable("STRIDE_EDITOR_ASSET_DIAGNOSTICS");
        return string.IsNullOrEmpty(path) ? null : new EditorAssetDiagnosticLog(Path.GetFullPath(path));
    }
    private void OnMessage(ILogMessage message)
    {
        if (message is not AssetLogMessage && message is not AssetSerializableLogMessage) return;
        var typed = message as AssetLogMessage;
        var serialized = message as AssetSerializableLogMessage;
        var id = typed?.AssetReference?.Id.ToString() ?? serialized?.AssetId.ToString();
        if (id == null) return;
        var line = JsonSerializer.Serialize(new
        {
            Id = id, Level = (int)message.Type, Module = message.Module, Text = typed?.TextWithoutLocation ?? message.Text,
            Exception = message.ExceptionInfo?.ToString(), File = typed?.File,
            Line = typed == null ? serialized!.Line : typed.Line + 1,
            Column = typed == null ? serialized!.Character : typed.Character + 1,
            AssetUrl = typed?.AssetReference?.Location ?? serialized?.AssetUrl?.ToString(),
            ExceptionType = message.ExceptionInfo?.TypeFullName,
        });
        lock (gate)
        {
            if (disposed) return;
            writer.WriteLine(line); writer.Flush();
        }
    }
    public void Dispose()
    {
        GlobalLogger.GlobalMessageLogged -= OnMessage;
        lock (gate) { if (disposed) return; disposed = true; writer.Dispose(); }
    }
}
