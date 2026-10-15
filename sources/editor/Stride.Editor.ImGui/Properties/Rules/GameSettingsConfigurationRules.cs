using System;
using System.Collections.Generic;
using System.Linq;
using Stride.Core;
using Stride.Data;

namespace Stride.Assets.Presentation.NodePresenters.Rules;

internal static class GameSettingsConfigurationRules
{
    internal static string Name(Type? type) => DisplayAttribute.GetDisplayName(type ?? typeof(Configuration));
    internal static bool CanAdd(IEnumerable<Configuration> configurations, Type type) => configurations.All(value => value.GetType() != type);
    internal static int? Order(int? parentOrder, int index) => parentOrder + index;
}
