using Hexa.NET.ImGui;
using Stride.Graphics;
using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorIcons : IDisposable
{
    private readonly ImGuiRenderer renderer;
    private readonly Dictionary<string, (Texture Texture, ImTextureRef Reference)> icons = [];
    private readonly Dictionary<string, string> stockIconPaths = [];
    private readonly List<(Texture Texture, ImTextureRef Reference)> failedStockImages = [];
    private bool disposing, disposed;
    public EditorIcons(GraphicsDevice device, ImGuiRenderer renderer)
    {
        this.renderer = renderer;
        iconDevice = device;
        foreach (var path in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "Icons"), "*.png"))
        {
            stockIconPaths[Path.GetFileNameWithoutExtension(path)] = path;
        }
        InitializeComponentIcons();
    }
    public bool Button(string name, string? tooltip = null, float logicalSize = 20)
    {
        EnsureImagesUsable();
        MarkProviderImageUsed(name);
        var size = logicalSize * Hexa.NET.ImGui.ImGui.GetStyle().FontScaleDpi;
        var clicked = Hexa.NET.ImGui.ImGui.ImageButton("toolbar/" + name, Get(name), new System.Numerics.Vector2(size));
        if (!string.IsNullOrEmpty(tooltip) && Hexa.NET.ImGui.ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            Hexa.NET.ImGui.ImGui.SetTooltip(tooltip);
        return clicked;
    }
    public ImTextureRef Get(string name) { EnsureImagesUsable(); MarkProviderImageUsed(name); return LoadStockIcon(name).Reference; }
    private bool HasImage(string name) => icons.ContainsKey(name) || stockIconPaths.ContainsKey(name);
    private (Texture Texture, ImTextureRef Reference) LoadStockIcon(string name)
    {
        if (icons.TryGetValue(name, out var cached)) return cached;
        using var stream = File.OpenRead(stockIconPaths[name]);
        var texture = Texture.Load(iconDevice, stream, loadAsSrgb: true);
        ImTextureRef reference = default;
        try
        {
            reference = renderer.RegisterTexture(texture);
            var loaded = (texture, reference); icons.Add(name, loaded); return loaded;
        }
        catch { failedStockImages.Add((texture, reference)); throw; }
    }
    public bool TextButton(string icon, string label, string? tooltip = null, float logicalSize = 16)
    {
        EnsureImagesUsable();
        var separator = label.IndexOf("##", StringComparison.Ordinal);
        var text = separator < 0 ? label : label[..separator];
        var identitySeparator = label.IndexOf("###", StringComparison.Ordinal);
        var identity = identitySeparator < 0 ? label : label[(identitySeparator + 3)..];
        var style = Gui.GetStyle();
        var imageSize = logicalSize * style.FontScaleDpi;
        var textSize = Gui.CalcTextSize(text);
        var size = new Vector2(style.FramePadding.X * 2 + imageSize + style.ItemInnerSpacing.X + textSize.X,
            style.FramePadding.Y * 2 + Math.Max(imageSize, textSize.Y));
        var clicked = Gui.Button("###" + identity, size);
        var minimum = Gui.GetItemRectMin();
        var imageMinimum = minimum + new Vector2(style.FramePadding.X, (size.Y - imageSize) * .5f);
        var draw = Gui.GetWindowDrawList();
        draw.AddImage(Get(icon), imageMinimum, imageMinimum + new Vector2(imageSize), Vector2.Zero, Vector2.One,
            Gui.GetColorU32(System.Numerics.Vector4.One));
        draw.AddText(minimum + new Vector2(style.FramePadding.X + imageSize + style.ItemInnerSpacing.X,
            (size.Y - textSize.Y) * .5f), Gui.GetColorU32(ImGuiCol.Text), text);
        if (!string.IsNullOrEmpty(tooltip) && Gui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) Gui.SetTooltip(tooltip);
        return clicked;
    }
    public System.Numerics.Vector2 Size(string name)
    {
        EnsureImagesUsable(); var image = LoadStockIcon(name); return new(image.Texture.Width, image.Texture.Height);
    }
    private void EnsureImagesUsable() => ObjectDisposedException.ThrowIf(disposing, this);
    public void Dispose()
    {
        if (disposed) return;
        EnsureComponentImagesDrained();
        disposing = true;
        DisposeComponentIcons();
        foreach (var entry in icons.ToArray())
        {
            renderer.UnregisterTexture(entry.Value.Reference); entry.Value.Texture.Dispose(); icons.Remove(entry.Key);
        }
        foreach (var entry in failedStockImages.ToArray())
        {
            renderer.UnregisterTexture(entry.Reference); entry.Texture.Dispose(); failedStockImages.Remove(entry);
        }
        stockIconPaths.Clear();
        disposed = true;
    }
}
