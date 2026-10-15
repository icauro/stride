using System;
using System.Threading;
using System.Threading.Tasks;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Diagnostics;
using Stride.Core.Serialization.Contents;
using Stride.Engine;
using Stride.Games;
using Stride.Graphics;
using Stride.Core.Mathematics;
using Stride.Input;

namespace Stride.Editor.Preview;

/// <summary>Native engine contract, distinct from the unchanged WPF IAssetPreview/domain identity.</summary>
public interface IAssetPreviewRuntime : IAsyncDisposable
{
    Texture? Output { get; }
    bool Visible { get; set; }
    ValueTask InitializeAsync(AssetPreviewRuntimeContext context, CancellationToken cancellation);
    ValueTask UpdateAssetAsync(AssetPreviewRuntimeContext context, CancellationToken cancellation);
    void Resize(int width, int height);
    void Update(GameTime time);
    void Render(GameTime time);
}

/// <summary>Actual pointer events for an explicitly selected plain engine-image surface.</summary>
public readonly record struct AssetPreviewPointerInput(Vector2 Position, Vector2 Delta, Vector2 ViewportSize,
    float Wheel, float HorizontalWheel, bool Left, bool Right, bool Middle, bool Control, bool Shift, bool Alt, bool Hovered, bool Active)
{
    public bool Extended1 { get; init; }
    public bool Extended2 { get; init; }
    public bool IsButtonDown(MouseButton button) => button switch
    {
        MouseButton.Left => Left,
        MouseButton.Right => Right,
        MouseButton.Middle => Middle,
        MouseButton.Extended1 => Extended1,
        MouseButton.Extended2 => Extended2,
        _ => false,
    };
}
public interface IAssetPreviewPointerRuntime
{
    void HandlePointer(AssetPreviewPointerInput input);
}
public readonly record struct AssetPreviewKeyboardInput(Keys Key, bool IsDown, bool IsRepeat, bool Control, bool Shift, bool Alt);
/// <summary>Explicit viewport-focused key/text delivery, independent of other editor panels.</summary>
public interface IAssetPreviewKeyboardRuntime
{
    void HandleKeyboard(AssetPreviewKeyboardInput input);
    void HandleText(string text);
}
public interface IAssetPreviewFocusRuntime
{
    void SetInputFocus(bool focused);
}
public interface IAssetPreviewViewStateRuntime
{
    object CaptureViewState();
    void RestoreViewState(object state);
}

/// <summary>Actual host engine/content services borrowed for one leased preview.</summary>
public sealed class AssetPreviewRuntimeContext
{
    public Game Game { get; }
    public AssetItem AssetItem { get; }
    /// <summary>Host-owned private cache, borrowed until this runtime's asynchronous disposal completes.</summary>
    /// <remarks>The host unloads the cache after real GPU completion and then releases its physical package lease.</remarks>
    public ContentManager Content { get; }
    public Logger Logger { get; }
    public IServiceRegistry Services => Game.Services;
    public GraphicsDevice GraphicsDevice => Game.GraphicsDevice;
    public GraphicsContext GraphicsContext => Game.GraphicsContext;
    private readonly Action<IDisposable> retire;
    private readonly Func<Action, CancellationToken, Task> invoke;
    public void RetireGraphicsResource(IDisposable resource) => retire(resource);
    public Task InvokeOnEditorAsync(Action action, CancellationToken cancellation = default) => invoke(action, cancellation);
    public AssetPreviewRuntimeContext(Game game, AssetItem assetItem, ContentManager content, Logger logger,
        Action<IDisposable> retire, Func<Action, CancellationToken, Task> invoke)
    { Game = game; AssetItem = assetItem; Content = content; Logger = logger; this.retire = retire; this.invoke = invoke; }
}

/// <summary>Explicit source adapter for an exact real provider object; no replacement IAssetPreview is synthesized.</summary>
public interface IAssetPreviewRuntimeAdapter : IAsyncDisposable
{
    ValueTask<IAssetPreviewRuntime> CreateAsync(object provider, AssetPreviewRuntimeContext context, CancellationToken cancellation);
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class AssetPreviewRuntimeAttribute<TAsset> : Attribute where TAsset : Asset
{
    /// <summary>Explicitly use the plain engine texture surface; no legacy WPF controls are inferred.</summary>
    public bool UseImageSurface { get; set; }
    public bool RequiresCompiledContent { get; set; } = true;
}

/// <summary>Attach an exact existing provider to a native source adapter.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class AssetPreviewRuntimeAdapterAttribute<TProvider, TAsset> : Attribute where TAsset : Asset
{
    public bool UseImageSurface { get; set; }
    public bool RequiresCompiledContent { get; set; } = true;
}
