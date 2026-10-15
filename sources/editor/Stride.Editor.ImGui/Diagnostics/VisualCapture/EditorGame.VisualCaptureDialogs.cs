using System.Runtime.InteropServices;
using System.Text.Json;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private readonly HashSet<string> captureOpenedDraftDialogs = [];

    private void DefineVisualCaptureDraftDialogs()
    {
        RegisterVisualCaptureTargetDefinition("dialogs.new-project", "modal", "Project selection", "###ProjectSelection");
        RegisterVisualCaptureTargetDefinition("dialogs.new-package", "modal", "New package", EditorLocalization.Widget("New package"));
        RegisterVisualCaptureTargetDefinition("dialogs.add-asset", "modal", "Add asset", "###AssetCreation");
        RegisterVisualCaptureTargetDefinition("dialogs.import-assets", "modal", "Import assets", "Import assets");
    }

    private bool RevealVisualCaptureDraftDialog(string target)
    {
        if (target is not ("dialogs.new-project" or "dialogs.new-package" or "dialogs.add-asset")) return false;
        OpenVisualCaptureDraftDialog(target, default);
        return true;
    }

    private unsafe bool TryVisualCaptureDraftDialogAction(string target, string action, JsonElement parameters, out object? result)
    {
        result = null;
        if (target is not ("dialogs.new-project" or "dialogs.new-package" or "dialogs.add-asset" or "dialogs.import-assets")) return false;
        if (action is "open" or "show" or "open-modal")
        {
            OpenVisualCaptureDraftDialog(target, parameters);
            result = new { opened = true, draftOnly = true, target };
            return true;
        }
        if (action is not ("cancel" or "close")) return false;
        if (!captureOpenedDraftDialogs.Contains(target)) throw new InvalidOperationException("This draft dialog was not opened by capture navigation.");
        if ((target == "dialogs.new-project" && projectCreationPanel?.IsGenerating == true)
            || (target == "dialogs.new-package" && addedProjectCreation?.IsGenerating == true)
            || (target == "dialogs.import-assets" && IsAssetImportUpdating))
            throw new InvalidOperationException("Capture cancellation is restricted to unaccepted draft dialogs.");
        var expected = target switch
        {
            "dialogs.new-project" => "###ProjectSelection",
            "dialogs.new-package" => EditorLocalization.Widget("New package"),
            "dialogs.add-asset" => "###AssetCreation",
            _ => "Import assets",
        };
        var stack = Gui.GetCurrentContext().OpenPopupStack;
        for (var index = stack.Size - 1; index >= 0; index--)
        {
            var window = stack[index].Window;
            if (window == null) continue;
            var name = Marshal.PtrToStringUTF8((nint)window->Name) ?? "";
            if (name != expected && !name.EndsWith(expected, StringComparison.Ordinal)) continue;
            switch (target)
            {
                case "dialogs.new-project": projectCreationPanel?.Cancel(); break;
                case "dialogs.new-package": addedProjectCreation?.Cancel(); break;
                case "dialogs.add-asset": ownedAssetCreation?.Clear(); break;
                case "dialogs.import-assets": assetImportOptionsPanel?.Clear(); break;
            }
            ImGuiP.ClosePopupToLevel(index, true);
            captureOpenedDraftDialogs.Remove(target);
            result = new { closed = true, saved = false, draftOnly = true };
            return true;
        }
        throw new InvalidOperationException("The requested draft modal is not open yet.");
    }

    private void OpenVisualCaptureDraftDialog(string target, JsonElement parameters)
    {
        if (Gui.GetCurrentContext().OpenPopupStack.Size != 0 || projectLoading != null || RecoveryTransitionBusy
            || IsAuthoringPipelineUpdating || IsProjectCreationUpdating || IsAddingSolutionProjects || IsAssetImportUpdating)
            throw new InvalidOperationException("Close the current modal and wait for active operations before opening a draft dialog.");
        switch (target)
        {
            case "dialogs.new-project": ProjectCreation.Open(); break;
            case "dialogs.new-package":
                if (!CanRequestSolutionProject) throw new InvalidOperationException("New package requires an editable loaded solution.");
                RequestNewSolutionProject(); break;
            case "dialogs.add-asset":
                if (!CanAddAsset) throw new InvalidOperationException("Select one editable asset folder before opening Add asset.");
                RequestAssetCreation(); break;
            case "dialogs.import-assets":
                if (!CanAddAsset || assetBrowser.SelectedFolder is not { } folder)
                    throw new InvalidOperationException("Select one editable asset folder before opening import options.");
                if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("files", out var values)
                    || values.ValueKind != JsonValueKind.Array) throw new ArgumentException("Import options require files containing actual existing source paths.");
                var files = values.EnumerateArray().Select(value => Path.GetFullPath(value.GetString() ?? "")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (files.Length == 0 || files.Any(file => !File.Exists(file) || GetAssetImportChoices(file, null).Length == 0))
                    throw new InvalidOperationException("Each source must exist and support the real import chooser; automatic import is not allowed by capture navigation.");
                RequestAssetImportOptions(files, folder.Package, folder.RelativePath); break;
            default: throw new InvalidOperationException("Unsupported draft modal.");
        }
        captureOpenedDraftDialogs.Add(target);
    }
}
