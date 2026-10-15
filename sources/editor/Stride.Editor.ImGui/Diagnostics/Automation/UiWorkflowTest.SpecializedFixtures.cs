using Stride.UI.Assets;
using Stride.Assets.Quantum;
using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Core.Diagnostics;
using Stride.Core.IO;
using Stride.Core.Reflection;
using Stride.UI.Controls;
using Stride.UI.Panels;

namespace Stride.Editor.ImGui;

internal sealed partial class UiWorkflowTest
{
    private PreviewFamilyFixtureVerification? spriteAuthoredFixture;
    private string?[] spriteSiblingNames = [];
    private string? uiAuthoredFixturePath;
    private static readonly Guid UiFixtureButtonId = new("d9fa5f52-5c9f-4472-ad07-88d6a2bf8a73");
    private const string UiFixtureName = "U06UI";

    // Test fixture setup only; invoked before the actual project-open UI action.
    internal void PrepareSpecializedAuthoredFixture(bool sprite)
    {
        if (sprite)
        {
            spriteAuthoredFixture = game.PreparePreviewFamilyFixture("sprite");
            spriteSiblingNames = AssetFileSerializer.Load<Stride.Assets.Sprite.SpriteSheetAsset>(spriteAuthoredFixture.AssetPath)
                .Asset.Sprites.Skip(1).Select(frame => frame.Name).ToArray();
            return;
        }
        uiAuthoredFixturePath = game.PrepareAuthoredUiFixture();
    }

    internal static Guid AuthoredUiButtonId => UiFixtureButtonId;
}

internal sealed partial class EditorGame
{
    internal string PrepareAuthoredUiFixture()
    {
        if (workspace != null || projectLoading != null) throw new InvalidOperationException("Prepare the UI fixture before loading.");
        var packagePath = Path.GetFullPath(projectPathInput);
        var privateRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Stride-ImGui-UI")) + Path.DirectorySeparatorChar;
        if (!packagePath.StartsWith(privateRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("UI fixture preparation requires a disposable private project copy.");
        var path = Path.Combine(Path.GetDirectoryName(packagePath)!, "Assets", "U06UI.sduipage");
        if (File.Exists(path)) throw new IOException("The private UI fixture already exists.");
        var page = new UIPageAsset();
        var root = (Grid)ObjectFactoryRegistry.NewInstance(typeof(Grid)); root.Name = "U06Root";
        var button = (Button)ObjectFactoryRegistry.NewInstance(typeof(Button));
        button.Id = UiWorkflowTest.AuthoredUiButtonId; button.Name = "U06Button"; button.Width = 180; button.Height = 48;
        button.BackgroundColor = new Stride.Core.Mathematics.Color((byte)196, (byte)64, (byte)48, (byte)255);
        root.Children.Add(button);
        page.Hierarchy.RootParts.Add(root);
        page.Hierarchy.Parts.Add(new UIElementDesign(root)); page.Hierarchy.Parts.Add(new UIElementDesign(button));
        AssetCollectionItemIdHelper.GenerateMissingItemIds(page);
        // Native hierarchy references require the same Quantum metadata as an
        // editor save; a bare serializer duplicates roots and regenerates IDs.
        AssetQuantumRegistry.RegisterAssembly(typeof(AssetPropertyGraph).Assembly);
        AssetQuantumRegistry.RegisterAssembly(typeof(UIAssetPropertyGraph).Assembly);
        var container = new AssetPropertyGraphContainer(new AssetNodeContainer { NodeBuilder = { NodeFactory = new AssetNodeFactory() } });
        var item = new AssetItem("U06UI", page);
        var log = new LoggerResult();
        using (var graph = (UIAssetPropertyGraph)container.InitializeAsset(item, log)!)
        {
            graph.Initialize(); graph.PrepareForSave(log, item);
            if (log.HasErrors) throw new InvalidOperationException("UI fixture graph metadata preparation failed: " + log.ToText());
            AssetFileSerializer.Save(path, page, item.YamlMetadata);
        }
        var reopened = AssetFileSerializer.Load<UIPageAsset>(path).Asset;
        if (reopened.Id != page.Id || reopened.Hierarchy.Parts.Count != 2 || reopened.Hierarchy.RootParts.Count != 1
            || !reopened.Hierarchy.Parts.TryGetValue(button.Id, out var persistedButton)
            || persistedButton.UIElement.Name != "U06Button"
            || !ReferenceEquals(reopened.Hierarchy.RootParts[0], reopened.Hierarchy.Parts[root.Id].UIElement)
            || !ReferenceEquals(reopened.Hierarchy.RootParts[0].VisualChildren.Single(), persistedButton.UIElement))
            throw new InvalidDataException("Independent UI fixture reload must preserve native part IDs and shared root/child ownership.");
        var package = AssetFileSerializer.Load<Package>(packagePath).Asset;
        var owner = new StandalonePackage(package);
        package.RootAssets.Add(new AssetReference(page.Id, owner.Qualify(new UFile("U06UI"))));
        AssetFileSerializer.Save(packagePath, package, null);
        return path;
    }
}
