using Stride.Audio;
using Stride.Core;
using Stride.Engine;
using Stride.Games;
using Stride.Rendering.Compositing;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private AuthorityParityVerification? authorityParityVerification;
    // Call once per owner-thread frame; ordinary retirement pumping stays in the host.
    // Parent runner supplies its normal bounded deadline and writes the returned evidence.
    internal bool StepAuthorityParityVerification()
    {
        if (!Environment.GetCommandLineArgs().Contains("--ui-test")
            && !Environment.GetCommandLineArgs().Contains("--verify-authority-parity"))
            throw new InvalidOperationException("Authority verification requires an explicit private verification launch.");
        return (authorityParityVerification ??= new(this)).Step();
    }
    internal void CancelAuthorityParityVerification() => authorityParityVerification?.RetireRemaining();
    internal object? AuthorityParityVerificationEvidence => authorityParityVerification?.Evidence;
}

internal sealed class AuthorityParityVerification(EditorGame host)
{
    private SceneViewportAuthority? first, second, replacement;
    private AuthorityParityComponent? firstProbe, secondProbe, replacementProbe;
    private readonly List<SceneViewportResources> retired = [];
    private Game? retiredGame;
    private Task<bool>? disposedRead;
    private int phase;
    internal object Evidence => new { phase, twoRealEditorProcessors = firstProbe?.Updates > 0 && secondProbe?.Updates > 0,
        replacementEditorProcessor = replacementProbe?.Updates > 0, retiredOwners = retired.Count,
        completedRetirements = retired.Count(owner => owner.IsDisposed), asynchronousDisposedReadRejected = disposedRead?.IsCompletedSuccessfully == true && disposedRead.Result };

    private (SceneViewportAuthority Authority, AuthorityParityComponent Probe) Create(string name)
    {
        var compositor = new GraphicsCompositor();
        var authority = new SceneViewportAuthority(host, host.Content);
        var probe = new AuthorityParityComponent();
        var entity = new Entity(name); entity.Add(probe);
        var scene = new Scene(); scene.Entities.Add(entity);
        try { authority.Initialize(scene, compositor); }
        catch { Retire(authority, compositor); throw; }
        compositors.Add(authority, compositor);
        return (authority, probe);
    }
    private readonly Dictionary<SceneViewportAuthority, GraphicsCompositor> compositors = [];
    private void Retire(SceneViewportAuthority authority, GraphicsCompositor? compositor = null)
    {
        var resources = new SceneViewportResources(host, null, null);
        authority.RetainForRetirement(resources);
        resources.Retain(compositor ?? compositors[authority]);
        resources.Retire(); retired.Add(resources);
        compositors.Remove(authority);
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    internal bool Step()
    {
        try { return StepCore(); }
        catch { RetireRemaining(); throw; }
    }
    internal void RetireRemaining()
    {
        foreach (var authority in compositors.Keys.ToArray()) Retire(authority);
    }
    private bool StepCore()
    {
        var time = new GameTime(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1d / 60));
        if (phase == 0)
        {
            try
            {
                (first, firstProbe) = Create("authority-first");
                (second, secondProbe) = Create("authority-second");
                first.Update(time); second.Update(time);
                Require(firstProbe.Updates > 0 && secondProbe.Updates > 0, "Both real Editor-mode processors must update.");
                Require(first.ViewGame.IsRunning && second.ViewGame.IsRunning, "Initialized native scene games must publish their actual running state.");
                Require(!ReferenceEquals(firstProbe.ObservedGame, secondProbe.ObservedGame)
                    && !ReferenceEquals(firstProbe.ObservedGame, host)
                    && !ReferenceEquals(firstProbe.ObservedScene, secondProbe.ObservedScene)
                    && !ReferenceEquals(firstProbe.ObservedSystems, secondProbe.ObservedSystems), "Scene game authorities must be independent.");
                Require(ReferenceEquals(first.ViewGame.GraphicsDevice, host.GraphicsDevice)
                    && ReferenceEquals(first.ViewGame.Font, host.Font) && ReferenceEquals(first.ViewGame.Services.GetSafeServiceAs<AudioSystem>(), host.Services.GetSafeServiceAs<AudioSystem>()), "Only device/font/audio resources are shared.");
                retiredGame = first.ViewGame;
                Retire(first); phase = 1;
                Require(!retiredGame.IsRunning && retiredGame.IsExiting && second.ViewGame.IsRunning,
                    "Stopping one native scene must publish only its own stopped lifecycle.");
            }
            catch
            {
                foreach (var authority in compositors.Keys.ToArray()) Retire(authority);
                throw;
            }
        }
        if (phase == 1)
        {
            second!.Update(time);
            Require(!second.ViewGame.IsDisposed && secondProbe!.Updates > 1, "Closing one authority must preserve its sibling.");
            if (!retired[0].IsDisposed) return false;
            Require(retiredGame!.IsDisposed && !host.IsDisposed, "Retirement releases only the actual view game.");
            disposedRead = Task.Run(() =>
            {
                try { _ = retiredGame.SceneSystem; return false; }
                catch (ObjectDisposedException) { }
                try { _ = retiredGame.GameSystems; return false; }
                catch (ObjectDisposedException) { }
                try { _ = retiredGame.Services; return false; }
                catch (ObjectDisposedException) { return true; }
            });
            (replacement, replacementProbe) = Create("authority-replacement");
            replacement.Update(time);
            Require(replacementProbe.Updates > 0 && !ReferenceEquals(replacement.ViewGame, retiredGame)
                && !ReferenceEquals(replacement.SceneSystem, first!.SceneSystem), "Recreation installs a fresh real scene authority.");
            Retire(second); Retire(replacement); phase = 2;
        }
        if (phase == 2)
        {
            if (retired.Any(owner => !owner.IsDisposed) || disposedRead?.IsCompleted != true) return false;
            Require(disposedRead.GetAwaiter().GetResult(), "An asynchronous stale owner must reject disposed game context reads.");
            Require(!host.IsDisposed && host.GraphicsDevice != null, "Native view closure must preserve the host.");
            phase = 3;
        }
        return phase == 3;
    }
}
