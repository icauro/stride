// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core;
using Stride.Core.IO;
using Stride.Games;
using Stride.Graphics.Font;

namespace Stride.Rendering.Fonts
{
    /// <summary>
    /// The game system in charge of calling <see cref="FontSystem"/>.
    /// </summary>
    public class GameFontSystem : GameSystemBase
    {
        public FontSystem FontSystem { get; private set; }

        private bool graphicsContentLoaded;

        public void ReleaseGraphicsResourcesForRecovery()
        {
            if (!graphicsContentLoaded) return;
            FontSystem.Unload();
            graphicsContentLoaded = false;
        }

        public void RebindGraphicsResourcesAfterRecovery()
        {
            if (graphicsContentLoaded) return;
            FontSystem.Load(GraphicsDevice, Services.GetSafeServiceAs<IDatabaseFileProviderService>());
            graphicsContentLoaded = true;
        }

        public GameFontSystem(IServiceRegistry registry)
            : base(registry)
        {
            Visible = true;
            FontSystem = new FontSystem();
        }

        public override void Draw(GameTime gameTime)
        {
            base.Draw(gameTime);

            FontSystem.Draw();
        }

        protected override void LoadContent()
        {
            base.LoadContent();

            RebindGraphicsResourcesAfterRecovery();
        }

        protected override void UnloadContent()
        {
            base.UnloadContent();

            ReleaseGraphicsResourcesForRecovery();
        }
    }
}
