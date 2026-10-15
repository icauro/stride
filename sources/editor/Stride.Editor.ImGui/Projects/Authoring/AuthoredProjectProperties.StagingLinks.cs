using Stride.Core.Assets;
namespace Stride.Editor.ImGui;

internal sealed partial class AuthoredProjectProperties
{
    private static void PrepareNativeImportedLinks(System.Xml.Linq.XDocument xml, string contextProperty)
    {
        // The SDK's AssignLinkMetadata task derives Link from the original
        // defining directory and item FullPath. Private input copies have a
        // different builtin origin, so carry only the certified relative suffix.
        // The task still executes and retains its native output/overwrite policy.
        foreach (var task in xml.Descendants().Where(element => element.Name.LocalName == "AssignLinkMetadata"
                     && element.Parent?.Name.LocalName == "Target"
                     && (string?)element.Parent.Attribute("Name") == "AssignLinkMetadata").ToArray())
        {
            var items = (string?)task.Attribute("Items");
            var type = new[] { "None", "Content", "Page", "ApplicationDefinition", "EmbeddedResource", "Resource" }
                .FirstOrDefault(candidate => items == "@(" + candidate + ")");
            if (type == null) continue;
            var ns = task.Name.Namespace;
            string Metadata(string name) => "%(" + type + "." + name + ")";
            var preparedPath = Metadata(contextProperty + "_PreparedFullPath");
            var link = Metadata(contextProperty + "_ImportedLink");
            var condition = "'" + Metadata("Link") + "' == '' And '" + link + "' != '' And '"
                + preparedPath + "' != '' And '" + Metadata("FullPath") + "' == '" + preparedPath + "'";
            var nativeCondition = (string?)task.Attribute("Condition");
            if (!string.IsNullOrWhiteSpace(nativeCondition)) condition = "(" + nativeCondition + ") And (" + condition + ")";
            task.AddBeforeSelf(new System.Xml.Linq.XElement(ns + "ItemGroup",
                new System.Xml.Linq.XElement(ns + type,
                    new System.Xml.Linq.XElement(ns + "Link", new System.Xml.Linq.XAttribute("Condition", condition), link))));
        }
    }
}
