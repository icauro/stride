using Stride.Editor.Preview;
using Vector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

/// <summary>Controls for an explicit native source port of one exact provider view.</summary>
public interface IImGuiAssetPreviewSurface : IAsyncDisposable
{
    // Runs inside the actual preview ImGui window on the editor actor. Draw
    // original provider controls and return where its real engine output belongs.
    AssetPreviewSurfaceRegion Draw(object provider, IAssetPreviewRuntime runtime);
}
public readonly record struct AssetPreviewSurfaceRegion(Vector2 Position, Vector2 Size);

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class ImGuiAssetPreviewSurfaceAttribute<TProvider> : Attribute { }
