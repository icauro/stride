// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md for more information.
using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.IO;
using Stride.Graphics;

namespace Stride.Editor.ImGui
{
    internal static class PlatformOrientation
    {
        public static UFile? GetPlatformOrientationFile(SolutionProject project)
        {
            if (project.FullPath == null) return null;
            return project.Platform switch
            {
                PlatformType.Android => UPath.Combine(project.FullPath.GetFullDirectory(), new UFile(project.Name + "Activity.cs")),
                PlatformType.iOS => UPath.Combine(project.FullPath.GetFullDirectory(), new UFile("Info.plist")),
                _ => null,
            };
        }

        public static string UpdateAndroidActivityOrientation(string activitySource, RequiredDisplayOrientation orientation)
        {
            var orientationString = orientation switch
            {
                RequiredDisplayOrientation.Default or RequiredDisplayOrientation.LandscapeLeft => "Android.Content.PM.ScreenOrientation.Landscape",
                RequiredDisplayOrientation.LandscapeRight => "Android.Content.PM.ScreenOrientation.ReverseLandscape",
                RequiredDisplayOrientation.Portrait => "Android.Content.PM.ScreenOrientation.Portrait",
                _ => throw new ArgumentOutOfRangeException(nameof(orientation)),
            };
            return Regex.Replace(activitySource, @"(\[Activity(?:.*[\n,\r]*)+?[\n,\r,\s]*ScreenOrientation\s*=\s*)([\w,\d,\.]+)(\s*,)", $"$1{orientationString}$3");
        }

        public static void UpdateIosOrientation(XDocument xmlDoc, RequiredDisplayOrientation orientation)
        {
            var orientationKey = xmlDoc.Descendants("key").FirstOrDefault(x => x.Value == "UISupportedInterfaceOrientations");
            var orientationElement = ((XElement)orientationKey?.NextNode)?.Descendants("string").FirstOrDefault();
            if (orientationElement == null) return;
            orientationElement.Value = orientation switch
            {
                RequiredDisplayOrientation.Default or RequiredDisplayOrientation.LandscapeRight => "UIInterfaceOrientationLandscapeRight",
                RequiredDisplayOrientation.LandscapeLeft => "UIInterfaceOrientationLandscapeLeft",
                RequiredDisplayOrientation.Portrait => "UIInterfaceOrientationPortrait",
                _ => throw new ArgumentOutOfRangeException(nameof(orientation)),
            };
        }
    }
}
