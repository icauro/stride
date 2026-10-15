using Stride.Assets.Scripts;
using Stride.Core.Assets;
using Stride.Core.IO;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void CreateScriptTemplateAsset(Package package, AssetBrowserFolder? selectedFolder, AssetCreationPanel.Choice choice, string name, string sourceNamespace)
    {
        if (package.Container is not SolutionProject || choice.Template?.AssetTypeName != nameof(ScriptSourceFileAsset))
            throw new InvalidOperationException("This script template requires an actual source project.");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(sourceNamespace))
            throw new InvalidOperationException("The names you entered are invalid or empty.");
        var reserved = new List<string> { nameof(AsyncScript), nameof(SyncScript), nameof(StartupScript) };
        reserved.AddRange(typeof(ScriptComponent).GetInterfaces().Select(type => type.Name));
        for (var type = typeof(ScriptComponent); type != null && type != typeof(object); type = type.BaseType) reserved.Add(type.Name);
        var className = Stride.Utilities.BuildValidClassName(name, reserved);
        var namespaceName = Stride.Utilities.BuildValidNamespaceName(sourceNamespace, reserved);
        var folder = selectedFolder?.IsCode == true ? selectedFolder : assetBrowser.Packages.First(root => ReferenceEquals(root.Package, package)).Children.First(child => child.IsCode);
        var prefix = folder.RelativePath.Length == 0 ? "" : folder.RelativePath.TrimEnd('/') + "/";
        className = Stride.Core.NamingHelper.ComputeNewName(className, candidate =>
        {
            var location = new UFile(prefix + candidate);
            var qualified = package.Container.Qualify(location);
            var path = Path.Combine(package.RootDirectory.ToOSPath(), (prefix + candidate).Replace('/', Path.DirectorySeparatorChar) + ScriptSourceFileAsset.Extension);
            return package.Assets.Find(qualified) != null || File.Exists(path) || Directory.Exists(path) || authoring!.Find(path) != null;
        }, "{0}{1}");
        var templatePath = Path.ChangeExtension(choice.Template.FullPath.ToOSPath(), ScriptSourceFileAsset.Extension);
        var text = File.ReadAllText(templatePath).Replace("##Namespace##", namespaceName, StringComparison.Ordinal)
            .Replace("##Scriptname##", className, StringComparison.Ordinal);
        var asset = (ScriptSourceFileAsset)choice.Factory.New(); asset.Text = text;
        var owner = workspace!;
        PasteSourceAssets([new AssetItem(new UFile(className), asset)], folder);
        if (preferences?.Current.AskBeforeSavingNewScripts == false) RequestPackageMetadataSave(saveDocuments: true);
        else AssetCreation.RequestScriptSave((save, always) =>
        {
            if (!ReferenceEquals(workspace, owner)) throw new InvalidOperationException("The script source session changed.");
            if (always && preferences != null)
            {
                var previous = preferences.Current.AskBeforeSavingNewScripts;
                preferences.Current.AskBeforeSavingNewScripts = false;
                if (!preferences.Save()) { preferences.Current.AskBeforeSavingNewScripts = previous; throw new IOException(preferences.Error); }
            }
            if (save) RequestPackageMetadataSave(saveDocuments: true);
        });
    }
}
