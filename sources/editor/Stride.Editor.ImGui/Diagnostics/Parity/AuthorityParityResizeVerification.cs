using Stride.Graphics;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private AuthorityParityResizeVerification? authorityResizeVerification;
    internal bool StepAuthorityResizeVerification()
    {
        if (!Environment.GetCommandLineArgs().Contains("--ui-test")
            && !Environment.GetCommandLineArgs().Contains("--verify-authority-parity"))
            throw new InvalidOperationException("Target verification requires an explicit private verification launch.");
        return (authorityResizeVerification ??= new(this, renderer ?? throw new InvalidOperationException("The native renderer is unavailable."))).Step();
    }
    internal void CancelAuthorityResizeVerification() => authorityResizeVerification?.Close();
    internal object? AuthorityResizeVerificationEvidence => authorityResizeVerification?.Evidence;
}

internal sealed class AuthorityParityResizeVerification(EditorGame host, ImGuiRenderer renderer)
{
    private EngineViewport? view;
    private Texture? first, second, clamped;
    private int phase;
    internal object Evidence => new { phase, firstSize = new[] { 16, 24 }, secondSize = new[] { 32, 48 },
        normalizedSize = new[] { 1, 1 }, allTargetsReleased = phase == 2 };
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    internal void Close() => view?.Dispose();
    internal bool Step()
    {
        try
        {
            if (phase == 0)
            {
                view = new EngineViewport(host, renderer);
                view.Resize(16, 24); first = view.Target;
                Require(first is { Width: 16, Height: 24 }, "The engine target must use requested physical dimensions.");
                view.Resize(16, 24);
                Require(ReferenceEquals(first, view.Target), "An unchanged target size must not allocate another generation.");
                view.Resize(32, 48); second = view.Target;
                Require(second is { Width: 32, Height: 48 } && !ReferenceEquals(first, second), "Resize must install a distinct native target generation.");
                view.Resize(-20, 0); clamped = view.Target;
                Require(clamped is { Width: 1, Height: 1 }, "A zero/minimized extent must normalize to a usable native target.");
                Close();
                try { view.Resize(4, 4); throw new InvalidOperationException("Disposed view accepted a fresh target."); }
                catch (ObjectDisposedException) { }
                phase = 1;
            }
            if (phase == 1)
            {
                if (!view!.IsResourceRetirementComplete) return false;
                Require(first!.IsDisposed && second!.IsDisposed && clamped!.IsDisposed,
                    "All exact old and current native target generations must release after certified retirement.");
                Require(!host.IsDisposed && !host.GraphicsDevice.IsDisposed, "Target retirement must retain the shared device.");
                phase = 2;
            }
            return phase == 2;
        }
        catch { Close(); throw; }
    }
}
