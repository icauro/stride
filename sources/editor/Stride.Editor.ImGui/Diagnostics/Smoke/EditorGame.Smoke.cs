using Stride.Engine;
using Stride.Games;
using Stride.Graphics;
using Stride.Core.Mathematics;
using Stride.Core.Diagnostics;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawPrototypePanels()
    {
        dockedPanels = 0;
        if (Gui.BeginMainMenuBar())
        {
            if (Gui.BeginMenu("View"))
            {
                Gui.MenuItem("Dear ImGui demo", "", ref showDemo);
                if (Gui.MenuItem("Reset layout")) resetLayout = true;
                Gui.EndMenu();
            }
            Gui.Text("Stride editor experiment");
            Gui.EndMainMenuBar();
        }
        var dockspace = Gui.DockSpaceOverViewport();
        if (resetLayout && CanCreateInitialLayout())
        {
            CreateSmokeLayout(dockspace);
            resetLayout = false;
        }
        SetInitialWindow(new NVector2(10, 40), new NVector2(220, 350));
        if (Gui.Begin("Hierarchy"))
        {
            if (Gui.IsWindowDocked()) dockedPanels++;
            Gui.Text("Procedural preview");
            Gui.Selectable("Cube", true);
            Gui.Separator();
            Gui.TextWrapped("Drag panel tabs to dock them. Project loading will follow this rendering prototype.");
        }
        ImGuiScopes.EndWindow();
        SetInitialWindow(new NVector2(240, 40), new NVector2(650, 480));
        if (Gui.Begin("Scene"))
        {
            if (Gui.IsWindowDocked()) dockedPanels++;
            var size = Gui.GetContentRegionAvail();
            if (size.X > 0 && size.Y > 0)
            {
                preview!.Resize((int)size.X, (int)size.Y);
                Gui.Image(preview.TextureRef, size);
            }
        }
        ImGuiScopes.EndWindow();
        SetInitialWindow(new NVector2(900, 40), new NVector2(330, 350));
        if (Gui.Begin("Inspector"))
        {
            if (Gui.IsWindowDocked()) dockedPanels++;
            Gui.Text("Cube");
            Gui.Checkbox("Animate", ref animate);
            Gui.DragFloat3("Rotation", ref preview!.Rotation, 0.5f);
            Gui.InputText("Notes", ref scratchText, 256);
            notesPosition = Gui.GetItemRectMin() + new NVector2(15, 10);
            Gui.TextWrapped("Preview controls are temporary; asset transactions are the next milestone.");
        }
        ImGuiScopes.EndWindow();
        SetInitialWindow(new NVector2(10, 530), new NVector2(880, 180));
        if (Gui.Begin("Console"))
        {
            if (Gui.IsWindowDocked()) dockedPanels++;
            Gui.Text("Hexa.NET.ImGui 2.2.9 / Stride graphics / Direct3D11");
            Gui.Text($"Frame {frames} | {Gui.GetIO().Framerate:F1} FPS");
            Gui.Text($"Input capture: mouse={Gui.GetIO().WantCaptureMouse}, keyboard={Gui.GetIO().WantCaptureKeyboard}");
        }
        ImGuiScopes.EndWindow();
        if (showDemo) Gui.ShowDemoWindow(ref showDemo);
    }

    private static unsafe void CreateSmokeLayout(uint dockspace)
    {
        // These are the smoke renderer's four real window identities. The
        // production layout owns different tool names and document groups.
        var viewport = Gui.GetMainViewport();
        ImGuiP.DockBuilderRemoveNode(dockspace);
        ImGuiP.DockBuilderAddNode(dockspace, (ImGuiDockNodeFlags)ImGuiDockNodeFlagsPrivate.Space);
        ImGuiP.DockBuilderSetNodePos(dockspace, viewport.WorkPos);
        ImGuiP.DockBuilderSetNodeSize(dockspace, viewport.WorkSize);
        uint center = dockspace, left, right, bottom;
        ImGuiP.DockBuilderSplitNode(center, ImGuiDir.Left, .22f, &left, &center);
        ImGuiP.DockBuilderSplitNode(center, ImGuiDir.Right, .32f, &right, &center);
        ImGuiP.DockBuilderSplitNode(center, ImGuiDir.Down, .25f, &bottom, &center);
        ImGuiP.DockBuilderDockWindow("Hierarchy", left);
        ImGuiP.DockBuilderDockWindow("Inspector", right);
        ImGuiP.DockBuilderDockWindow("Console", bottom);
        ImGuiP.DockBuilderDockWindow("Scene", center);
        ImGuiP.DockBuilderFinish(dockspace);
    }

    // Synthetic events exercise Dear ImGui hit testing and text editing; physical device
    // input is supplied separately by ImGuiInput during normal interactive runs.
    private void UpdateSmokeTest()
    {
        var io = Gui.GetIO();
        io.AddFocusEvent(true);
        if (frames is >= 6 and <= 11) io.AddMousePosEvent(notesPosition.X, notesPosition.Y);
        if (frames == 6) io.AddMouseButtonEvent(0, true);
        if (frames == 7) io.AddMouseButtonEvent(0, false);
        if (frames == 8)
        {
            io.AddKeyEvent(ImGuiKey.ModCtrl, true);
            io.AddKeyEvent(ImGuiKey.A, true);
        }
        if (frames == 9)
        {
            io.AddKeyEvent(ImGuiKey.ModCtrl, false);
            io.AddKeyEvent(ImGuiKey.A, false);
        }
        if (frames == 10)
            foreach (var character in "Smoke typed") io.AddInputCharacter(character);
        if (frames == 60)
        {
            initialViewportWidth = preview!.Target!.Width;
            GraphicsDeviceManager.PreferredBackBufferWidth = 1100;
            GraphicsDeviceManager.PreferredBackBufferHeight = 760;
            GraphicsDeviceManager.ApplyChanges();
        }
    }
}
