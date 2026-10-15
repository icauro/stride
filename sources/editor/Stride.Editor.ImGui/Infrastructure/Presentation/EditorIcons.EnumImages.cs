using System.Text.Json;
using System.Runtime.CompilerServices;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorIcons
{
    private sealed record NativeEnumImage(string Assembly, string Type, string Value, string Icon);
    private sealed record EnumImageFamily(NativeEnumImage[] Images);
    private readonly ConditionalWeakTable<Type, EnumImageFamily> enumImageFamilies = new();
    private NativeEnumImage[]? nativeEnumImages;
    private NativeEnumImage[] NativeEnumImages => nativeEnumImages ??= ReadNativeEnumImages();
    private static NativeEnumImage[] ReadNativeEnumImages()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Icons", "native-enum-images.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<NativeEnumImage[]>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [] : [];
    }
    private EnumImageFamily EnumFamily(Type type)
    {
        if (enumImageFamilies.TryGetValue(type, out var cached)) return cached;
        var assembly = type.Assembly.GetName().Name;
        var family = new EnumImageFamily(NativeEnumImages.Where(image => image.Type == type.FullName
            && (image.Assembly == assembly || IsLinkedNativeEnumImage(type, image)) && HasImage(image.Icon)).ToArray());
        enumImageFamilies.Add(type, family); return family;
    }
    private static bool IsLinkedNativeEnumImage(Type type, NativeEnumImage image)
        => type == typeof(Stride.Assets.Presentation.AssetEditors.GameEditor.TransformationSpace)
            && image.Assembly == "Stride.Assets.Presentation.Wpf"
            && image.Type == "Stride.Assets.Presentation.AssetEditors.GameEditor.TransformationSpace";
    internal bool HasEnumImages(Type type)
    {
        CheckComponentImageThread();
        if (componentImageProviders?.HasEnumImages(type) == true) return true;
        return EnumFamily(type).Images.Length != 0;
    }
    internal string? EnumIcon(Enum value)
    {
        CheckComponentImageThread();
        if (componentImageDraining) return null;
        if (providerImages.TryGetValue(value, out var cached)) { cached.UsedFrame = Gui.GetFrameCount(); return cached.Key; }
        if (componentImageProviders?.AcquireEnumImage(value) is { } lease) return RegisterProviderImage(value, lease);
        var type = value.GetType();
        var name = value.ToString();
        var stock = EnumFamily(type).Images.FirstOrDefault(image => image.Value == name);
        return stock != null && HasImage(stock.Icon) ? stock.Icon : null;
    }
    // Actual ImageEnum template wraps 24-DIP choices with 16-DIP artwork.
    // Missing artwork for a registered family's value remains an empty choice,
    // as the original EnumToResource binding does.
    internal bool EnumImageButton(Enum value, bool selected, bool mixed = false)
    {
        var scale = Gui.GetStyle().FontScaleDpi; var size = new NVector2(24, 20) * scale;
        if (selected && !mixed) Gui.PushStyleColor(ImGuiCol.Button, Gui.GetStyle().Colors[(int)ImGuiCol.Header]);
        var clicked = Gui.Button("##Enum/" + value, size);
        if (selected && !mixed) Gui.PopStyleColor();
        var minimum = Gui.GetItemRectMin();
        var image = EnumIcon(value);
        if (image != null)
        {
            var imageSize = new NVector2(16) * scale; var origin = minimum + (size - imageSize) * .5f;
            Gui.GetWindowDrawList().AddImage(Get(image), origin, origin + imageSize);
        }
        if (Gui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) Gui.SetTooltip(value.ToString());
        return clicked;
    }
}
