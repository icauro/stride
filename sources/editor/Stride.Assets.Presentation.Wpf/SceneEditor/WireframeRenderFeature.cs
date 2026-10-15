// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.Game;
using Stride.Rendering;

namespace Stride.Assets.Presentation.SceneEditor
{
    /// <summary>WPF service adapter for the shared mesh wireframe implementation.</summary>
    public class WireframeRenderFeature : EditorWireframeRenderFeature
    {
        private IEditorGameEntitySelectionService selectionService;
        public void RegisterSelectionService(IEditorGameEntitySelectionService service)
        {
            if (service == null) throw new ArgumentNullException(nameof(service));
            if (selectionService != null) selectionService.SelectionUpdated -= SelectionUpdated;
            selectionService = service;
            DisplaySelectionMask = () => selectionService.DisplaySelectionMask;
            selectionService.SelectionUpdated += SelectionUpdated;
        }
        private void SelectionUpdated(object sender, EntitySelectionEventArgs e) => NotifySelectionChanged();
        protected override void Destroy()
        {
            if (selectionService != null) selectionService.SelectionUpdated -= SelectionUpdated;
            selectionService = null; DisplaySelectionMask = null;
            base.Destroy();
        }
    }
}
