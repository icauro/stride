using Android.App;
using Android.Content.PM;

using Stride.Engine;
using Stride.Starter;

namespace ComputeSkinningSample.Android;

[Activity(MainLauncher = true,
          Label = "Compute Skinning",
          ScreenOrientation = ScreenOrientation.Landscape,
          Theme = "@android:style/Theme.NoTitleBar.Fullscreen",
          ConfigurationChanges = ConfigChanges.UiMode | ConfigChanges.Orientation | ConfigChanges.KeyboardHidden | ConfigChanges.ScreenSize)]
public class ComputeSkinningSampleActivity : StrideActivity
{
    protected Game Game;

    protected override void OnRun()
    {
        base.OnRun();

        Game = new Game();
        Game.Run(GameContext);
    }

    protected override void OnDestroy()
    {
        Game.Dispose();

        base.OnDestroy();
    }
}
