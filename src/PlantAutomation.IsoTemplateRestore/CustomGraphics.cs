// Keeps editable model-space graphics in a hidden block definition inside Iso.dwt.
// The definition is only a snapshot; users work with separate entities.
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using System.Globalization;

namespace PlantAutomation.IsoTemplateRestore;

internal static class CustomGraphics
{
    internal const string Name = "PA_ISO_CUSTOM_GRAPHICS";

    internal static int SnapshotEntityCount(Document doc)
    {
        using var tr = doc.Database.TransactionManager.StartTransaction();
        var table = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
        if (!table.Has(Name)) return -1;
        var definition = (BlockTableRecord)tr.GetObject(table[Name], OpenMode.ForRead);
        return definition.Cast<ObjectId>().Count(id => !id.IsErased);
    }

    // Save replaces the snapshot even when the user deliberately saved no graphics.
    internal static int Save(Document doc)
    {
        using var docLock = doc.LockDocument();
        using var tr = doc.Database.TransactionManager.StartTransaction();
        var table = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
        var space = (BlockTableRecord)tr.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForRead);
        var source = new List<Entity>();
        foreach (ObjectId id in space)
        {
            if (id.IsErased || tr.GetObject(id, OpenMode.ForRead, false) is not Entity entity || !IsCustomerGraphics(entity)) continue;
            source.Add(entity);
        }

        BlockTableRecord definition;
        if (table.Has(Name))
        {
            definition = (BlockTableRecord)tr.GetObject(table[Name], OpenMode.ForWrite);
            foreach (ObjectId id in definition)
                if (!id.IsErased) tr.GetObject(id, OpenMode.ForWrite).Erase();
        }
        else
        {
            table.UpgradeOpen();
            definition = new BlockTableRecord { Name = Name, Origin = Point3d.Origin };
            table.Add(definition);
            tr.AddNewlyCreatedDBObject(definition, true);
        }

        var count = 0;
        foreach (var entity in source)
        {
            var clone = entity.Clone() as Entity ??
                throw new InvalidOperationException($"Cannot clone custom graphics entity {entity.GetType().FullName}.");
            definition.AppendEntity(clone);
            tr.AddNewlyCreatedDBObject(clone, true);
            count++;
        }
        tr.Commit();
        return count;
    }

    internal static int Restore(Document doc)
    {
        using var docLock = doc.LockDocument();
        using var tr = doc.Database.TransactionManager.StartTransaction();
        var table = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
        if (!table.Has(Name)) return 0;
        var space = (BlockTableRecord)tr.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
        // Autodesk can leave some model-space entities in place. Match them as
        // a multiset so the restore adds only the missing copies.
        var existing = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (ObjectId id in space)
        {
            if (id.IsErased || tr.GetObject(id, OpenMode.ForRead, false) is not Entity entity || !IsCustomerGraphics(entity)) continue;
            var key = GeometryKey(entity);
            if (key is not null) existing[key] = existing.GetValueOrDefault(key) + 1;
        }

        var definition = (BlockTableRecord)tr.GetObject(table[Name], OpenMode.ForRead);
        if (!definition.Cast<ObjectId>().Any(id => !id.IsErased)) return 0;
        using var reference = new BlockReference(Point3d.Origin, table[Name]);
        space.AppendEntity(reference);
        tr.AddNewlyCreatedDBObject(reference, true);
        var exploded = new DBObjectCollection();
        reference.Explode(exploded);
        var count = 0;
        foreach (DBObject item in exploded)
        {
            if (item is not Entity entity) { item.Dispose(); continue; }
            if (!IsCustomerGraphics(entity))
            {
                // Older snapshots may contain Plant's Drawing Area overlays.
                // Plant recreates these from IsoConfig.xml itself.
                entity.Dispose();
                continue;
            }
            var key = GeometryKey(entity);
            if (key is not null && existing.TryGetValue(key, out var available) && available > 0)
            {
                existing[key] = available - 1;
                entity.Dispose();
                continue;
            }
            space.AppendEntity(entity);
            tr.AddNewlyCreatedDBObject(entity, true);
            count++;
        }
        reference.Erase();
        tr.Commit();
        return count;
    }

    private static string? GeometryKey(Entity entity)
    {
        try
        {
            static string F(double n) => Math.Round(n, 6).ToString("R", CultureInfo.InvariantCulture);
            static string P(Point3d p) => string.Join(",", F(p.X), F(p.Y), F(p.Z));
            static string V(Vector3d v) => string.Join(",", F(v.X), F(v.Y), F(v.Z));
            var geometry = entity switch
            {
                Line line => $"{P(line.StartPoint)}|{P(line.EndPoint)}",
                Polyline polyline => string.Join("|", new[] { $"closed={polyline.Closed}", $"elevation={F(polyline.Elevation)}", $"normal={V(polyline.Normal)}" }
                    .Concat(Enumerable.Range(0, polyline.NumberOfVertices).Select(i =>
                        $"{P(polyline.GetPoint3dAt(i))},{F(polyline.GetBulgeAt(i))},{F(polyline.GetStartWidthAt(i))},{F(polyline.GetEndWidthAt(i))}"))),
                Circle circle => $"{P(circle.Center)}|{F(circle.Radius)}|{V(circle.Normal)}",
                Arc arc => $"{P(arc.Center)}|{F(arc.Radius)}|{F(arc.StartAngle)}|{F(arc.EndAngle)}|{V(arc.Normal)}",
                DBText dbText => $"{P(dbText.Position)}|{P(dbText.AlignmentPoint)}|{dbText.TextString}|{F(dbText.Height)}|{F(dbText.Rotation)}|{F(dbText.WidthFactor)}|{dbText.TextStyleName}|{dbText.Justify}|{V(dbText.Normal)}",
                MText mText => $"{P(mText.Location)}|{mText.Contents}|{F(mText.TextHeight)}|{F(mText.Width)}|{F(mText.Rotation)}|{mText.TextStyleName}|{mText.Attachment}|{V(mText.Normal)}",
                _ => null
            };
            if (geometry is null) return null;
            return string.Join("|", entity.GetType().FullName, entity.Layer, entity.ColorIndex, entity.Linetype,
                F(entity.LinetypeScale), entity.LineWeight, geometry);
        }
        catch
        {
            // Unknown or unreadable geometry is restored rather than skipped.
            return null;
        }
    }

    internal static bool IsCustomerGraphics(Entity entity)
    {
        if (entity is BlockReference or Table or Viewport) return false;
        if (string.Equals(entity.Layer, "Table", StringComparison.OrdinalIgnoreCase)) return false;
        if (entity.Layer.StartsWith("Area-", StringComparison.OrdinalIgnoreCase)) return false;
        // AutoCAD drafting entities live in AcDbMgd. Plant custom entities use
        // other assemblies and must not be turned into ordinary drawing entities.
        return entity.GetType().Assembly == typeof(Line).Assembly;
    }
}
