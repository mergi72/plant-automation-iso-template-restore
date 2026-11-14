// Stores customer block placements and attribute text beside each Iso.dwt.
// Plant 3D system blocks remain owned by IsoConfig.xml and Project Setup.
using System.Globalization;
using System.Xml.Linq;
using Autodesk.AutoCAD.Geometry;

namespace PlantAutomation.IsoTemplateRestore;

internal sealed record AttributeSpec(string Tag, string? Value, string? Source, string? FieldCode = null);
internal sealed record BlockSpec(string Name, Point3d Position, double Rotation, IReadOnlyList<AttributeSpec>? Attributes = null);

internal static class BlockConfig
{
    internal const string FileName = "PlantAutomation.IsoBlocks.xml";
    internal static string PathFor(string drawingPath) => Path.Combine(Path.GetDirectoryName(drawingPath)!, FileName);

    internal static int? LoadGraphicsCount(string drawingPath)
    {
        var path = PathFor(drawingPath);
        if (!File.Exists(path)) return null;
        var root = XDocument.Load(path).Root;
        if (root?.Name != "IsoBlocks") throw new InvalidDataException("Expected <IsoBlocks> root.");
        var element = root.Element("CustomGraphics");
        if (element is null) return null;
        if ((string?)element.Attribute("name") != CustomGraphics.Name)
            throw new InvalidDataException("Unexpected custom graphics definition name.");
        var raw = (string?)element.Attribute("entityCount");
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var count))
            throw new InvalidDataException("Invalid custom graphics entity count.");
        return count;
    }

    internal static IReadOnlyList<BlockSpec> Load(string drawingPath)
    {
        var path = PathFor(drawingPath);
        if (!File.Exists(path)) return [];
        var root = XDocument.Load(path).Root;
        if (root?.Name != "IsoBlocks") throw new InvalidDataException("Expected <IsoBlocks> root.");
        var result = new List<BlockSpec>();
        foreach (var element in root.Elements("Block"))
        {
            var name = ((string?)element.Attribute("name"))?.Trim();
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("Block name is empty.");
            double Read(string key, double fallback = 0)
            {
                var raw = (string?)element.Attribute(key);
                return raw is null ? fallback : double.Parse(raw, CultureInfo.InvariantCulture);
            }
            var attributes = element.Element("Attributes")?.Elements("Attribute")
                .Select(a => new AttributeSpec(
                    ((string?)a.Attribute("tag"))?.Trim() ?? "",
                    (string?)a.Attribute("value"),
                    (string?)a.Attribute("source"),
                    (string?)a.Attribute("fieldCode")))
                .Where(a => a.Tag.Length > 0).ToList();
            result.Add(new BlockSpec(name, new Point3d(Read("x"), Read("y"), Read("z")), Read("rotation"), attributes));
        }
        return result;
    }

    internal static string? TitleBlockName(string drawingPath)
    {
        var path = Path.Combine(Path.GetDirectoryName(drawingPath)!, "IsoConfig.xml");
        if (!File.Exists(path)) return null;
        return (string?)XDocument.Load(path).Descendants("TitleBlock").FirstOrDefault()?.Attribute("Symbol");
    }

    internal static string? NorthArrowName(string drawingPath)
    {
        var path = Path.Combine(Path.GetDirectoryName(drawingPath)!, "IsoConfig.xml");
        if (!File.Exists(path)) return null;
        return (string?)XDocument.Load(path).Descendants("NorthArrow").FirstOrDefault()?.Attribute("Symbol");
    }

    internal static int ReplaceFromDrawing(string drawingPath, IReadOnlyList<BlockSpec> actual, int? graphicsCount = null)
    {
        var path = PathFor(drawingPath);
        var root = new XElement("IsoBlocks");
        var savedGraphicsCount = graphicsCount ?? LoadGraphicsCount(drawingPath);
        if (savedGraphicsCount is { } entityCount)
            root.Add(new XElement("CustomGraphics",
                new XAttribute("name", CustomGraphics.Name),
                new XAttribute("entityCount", entityCount)));
        foreach (var block in actual)
        {
            var element = new XElement("Block",
                new XAttribute("name", block.Name),
                new XAttribute("x", block.Position.X.ToString("R", CultureInfo.InvariantCulture)),
                new XAttribute("y", block.Position.Y.ToString("R", CultureInfo.InvariantCulture)),
                new XAttribute("z", block.Position.Z.ToString("R", CultureInfo.InvariantCulture)),
                new XAttribute("rotation", block.Rotation.ToString("R", CultureInfo.InvariantCulture)));
            if (block.Attributes is { Count: > 0 })
                element.Add(new XElement("Attributes", block.Attributes.Select(a =>
                    new XElement("Attribute", new XAttribute("tag", a.Tag),
                        a.Value is null ? null : new XAttribute("value", a.Value),
                        a.Source is null ? null : new XAttribute("source", a.Source),
                        a.FieldCode is null ? null : new XAttribute("fieldCode", a.FieldCode)))));
            root.Add(element);
        }
        // Write a complete replacement so a failed write cannot leave partial XML.
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            new XDocument(root).Save(temporary);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return actual.Count;
    }
}
