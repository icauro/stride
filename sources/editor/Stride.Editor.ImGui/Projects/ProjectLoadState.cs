namespace Stride.Editor.ImGui;

internal sealed record ProjectLoadState(string Phase, string Detail, string? LogDirectory = null, int? ProcessId = null, string? OutputLine = null, BuildDiagnostic? Diagnostic = null,
    CompilerRunIdentity? CompilerRun = null, CompilerRunEvent CompilerEvent = CompilerRunEvent.None, NativeAssetDiagnostic? AssetDiagnostic = null);
