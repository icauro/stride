using Stride.Core;
using Stride.Core.Serialization.Contents;
using Stride.Engine;
using Stride.Engine.Design;
using Stride.Engine.Processors;
using Stride.Games;
using Stride.Graphics;
using Stride.Navigation;
using Stride.Physics;
using Stride.Physics.Engine;
using Stride.Rendering;
using Stride.Rendering.Compositing;

namespace Stride.Editor.ImGui;

/// <summary>Actual scene/service/render authority for one concurrently presented editor view.</summary>
internal sealed class SceneViewportAuthority : ISceneRendererContext, IDisposable
{
    private readonly Game game;
    internal Game ViewGame { get; private set; } = null!;
    private readonly RenderContext? rendering;
    private readonly RenderDrawContext? targetContext;
    private readonly ViewSceneSystem system = null!;
    private readonly List<IGameSystemBase> retiringSystems = [];
    private bool runtimeReleased, renderingReleased, servicesReleased;
    private GameTime drawTime = new();
    private Task? workerDrain;
    private bool stopping;
    internal bool IsStopping => stopping;
    internal bool IsReleased => servicesReleased;
    internal bool IsShutdownComplete { get; private set; }
    public ServiceRegistry Services { get; }
    public GameSystemCollection GameSystems { get; }
    public SceneSystem SceneSystem => system;
    public GraphicsDevice GraphicsDevice => game.GraphicsDevice;
    public GraphicsContext GraphicsContext => game.GraphicsContext;
    public ContentManager Content { get; }
    public GameTime DrawTime => drawTime;

    internal SceneViewportAuthority(Game game, ContentManager content)
    {
        this.game = game; Content = content;
        Services = new ServiceRegistry(game.Services, [typeof(IGame), typeof(Game), typeof(SceneSystem), typeof(IGameSystemCollection), typeof(ScriptSystem),
            typeof(IPhysicsSystem), typeof(PhysicsShapesRenderingService), typeof(DynamicNavigationMeshSystem), typeof(RenderContext)]);
        GameSystems = new GameSystemCollection(Services);
        try
        {
            Services.AddService<IGameSystemCollection>(GameSystems);
            Services.AddService(content); Services.AddService<IContentManager>(content);
            ViewGame = new Game(game, Services, GameSystems, content, registry => new ViewSceneSystem(registry));
            Services.AddService(ViewGame);
            system = (ViewSceneSystem)ViewGame.SceneSystem;
            GameSystems.Add(system);
            GameSystems.Insert(0, ViewGame.Script);
            rendering = RenderContext.Create(Services); Services.AddService(rendering);
            targetContext = new RenderDrawContext(Services, rendering, game.GraphicsContext);
        }
        catch (Exception failure)
        {
            try { Dispose(); }
            catch (Exception recovery) { throw new AggregateException("Scene authority initialization and cleanup failed.", failure, recovery); }
            throw;
        }
    }

    internal void Initialize(Scene scene, GraphicsCompositor compositor)
    {
        system.GraphicsCompositor.Dispose(); system.GraphicsCompositor = compositor; system.BorrowedCompositor = true;
        // SceneInstance installs native processors immediately. Their lazily
        // added game systems (including Bullet) must initialize on registration
        // before processors create simulations, as in the WPF editor lifecycle.
        GameSystems.Initialize();
        system.SceneInstance = new SceneInstance(Services, scene, ExecutionMode.Editor);
        GameSystems.LoadContent();
        ViewGame.SetSceneContextRunning(true);
    }

    internal void Update(GameTime time) { if (!runtimeReleased && !stopping) { ViewGame.AdvanceSceneContextTime(time, false); GameSystems.Update(time); } }
    internal void Draw(GameTime time) { if (!runtimeReleased && !stopping) { drawTime = time; ViewGame.AdvanceSceneContextTime(time, true); GameSystems.Draw(time); } }
    internal void Render(GameTime time, Texture? depth, Texture target)
    {
        var command = GraphicsContext.CommandList;
        var previous = command.Viewport;
        using (targetContext!.PushRenderTargetsAndRestore())
        {
            command.ResourceBarrierTransition(target, BarrierLayout.RenderTarget);
            command.SetRenderTargetAndViewport(depth, target);
            try { Draw(time); }
            finally { command.SetViewport(previous); }
        }
        command.ResourceBarrierTransition(target, BarrierLayout.ShaderResource);
    }
    internal void PrepareCapture(GameTime time) => drawTime = time;

    internal Task BeginShutdown()
    {
        if (workerDrain != null) return workerDrain;
        stopping = true; if (system != null) system.Visible = false;
        if (ViewGame != null && !ViewGame.IsDisposed) ViewGame.SetSceneContextRunning(false);
        var workers = GameSystems.OfType<DynamicNavigationMeshSystem>().Select(system => system.BeginShutdown()).ToArray();
        var scheduler = Services.GetService<ScriptSystem>()?.Scheduler;
        if (scheduler != null)
            foreach (var thread in scheduler.MicroThreads.ToArray()) if (!thread.IsOver) thread.Cancel();
        return workerDrain = Task.WhenAll(workers);
    }

    // Only the requesting view's native scheduler is pumped, on its ownership actor.
    // Worker completion does not require late graph/render publication or a normal Update.
    internal bool PumpShutdown()
    {
        if (IsShutdownComplete) return true;
        if (!BeginShutdown().IsCompleted) return false;
        var scheduler = Services.GetService<ScriptSystem>()?.Scheduler;
        if (scheduler == null) return IsShutdownComplete = true;
        scheduler.Run();
        return IsShutdownComplete = scheduler.MicroThreads.All(thread => thread.IsOver);
    }

    internal void RetainForRetirement(SceneViewportResources retirement)
    {
        retirement.RetainDrain(BeginShutdown(), PumpShutdown);
        retirement.Retain(new RuntimeRetirement(this));
        // Native compositor/render features remain borrowed from this view's object cache.
        // Destroy their cache before releasing the associated render context/service scope.
        retirement.RetainAfterCache(this);
    }
    private void ReleaseRuntime()
    {
        if (runtimeReleased) return;
        if (!PumpShutdown()) throw new PreviewResourceDrainPendingException();
        if (retiringSystems.Count == 0) retiringSystems.AddRange(GameSystems);
        // Release entity processors while all scene-local services are still available.
        if (!sceneReleased) { system?.Dispose(); sceneReleased = true; }
        while (retiringSystems.Count > 0)
        {
            var current = retiringSystems[^1];
            GameSystems.Remove(current);
            if (!ReferenceEquals(current, system)) (current as IDisposable)?.Dispose();
            retiringSystems.RemoveAt(retiringSystems.Count - 1);
        }
        runtimeReleased = true;
    }
    private bool sceneReleased;
    private bool targetContextReleased;
    public void Dispose()
    {
        ReleaseRuntime();
        if (!targetContextReleased) { targetContext?.Dispose(); targetContextReleased = true; }
        if (!renderingReleased) { rendering?.Dispose(); renderingReleased = true; }
        ViewGame?.Dispose();
        if (!servicesReleased) { Services.Dispose(); servicesReleased = true; }
    }
    private sealed class RuntimeRetirement(SceneViewportAuthority owner) : IDisposable
    { public void Dispose() => owner.ReleaseRuntime(); }
    private sealed class ViewSceneSystem(IServiceRegistry services) : SceneSystem(services)
    {
        internal bool BorrowedCompositor;
        protected override void Destroy()
        {
            // The canonical compiled content cache owns this compositor; base still releases
            // the one actual SceneInstance it created. Do not dispose cached assets twice.
            if (BorrowedCompositor) GraphicsCompositor = null;
            base.Destroy();
            renderDrawContext?.Dispose(); renderDrawContext = null;
        }
    }
}
