// Restores customer ISO template content after Project Setup rebuilds Iso.dwt.
// No Autodesk assembly or Plant 3D system entity is modified by this add-in.
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using System.Diagnostics;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: ExtensionApplication(typeof(PlantAutomation.IsoTemplateRestore.IsoTemplateRestore))]
[assembly: CommandClass(typeof(PlantAutomation.IsoTemplateRestore.IsoTemplateRestore))]

namespace PlantAutomation.IsoTemplateRestore;

public sealed class IsoTemplateRestore : IExtensionApplication
{
    private static readonly Dictionary<Document, DocumentTracker> Trackers = [];
    private static readonly Dictionary<Document, int> Pending = [];
    private static bool _started;
    private static bool _restoring;
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PlantAutomation", "iso-template-restore.log");

    public void Initialize()
    {
        if (_started) return;
        _started = true;
        var docs = AcApplication.DocumentManager;
        docs.DocumentCreated += OnCreated;
        docs.DocumentToBeDestroyed += OnDestroyed;
        foreach (Document doc in docs) Attach(doc);
        AcApplication.Idle += OnIdle;
        Log("started");
    }

    public void Terminate()
    {
        if (!_started) return;
        _started = false;
        var docs = AcApplication.DocumentManager;
        docs.DocumentCreated -= OnCreated;
        docs.DocumentToBeDestroyed -= OnDestroyed;
        AcApplication.Idle -= OnIdle;
        foreach (var tracker in Trackers.Values) tracker.Detach();
        Trackers.Clear();
        Pending.Clear();
    }

    [CommandMethod("P3DAUTOISOBLOCKSTATUS", CommandFlags.Modal)]
    public void Status()
    {
        var doc = AcApplication.DocumentManager.MdiActiveDocument;
        if (doc is null) return;
        var blocks = BlockConfig.Load(doc.Name);
        doc.Editor.WriteMessage($"\nISO block restore: active; drawing={doc.Name}; configured={blocks.Count}; pending={Pending.ContainsKey(doc)}; log={LogPath}\n");
        doc.Editor.WriteMessage($"  {CustomGraphics.Name}: xmlEntities={BlockConfig.LoadGraphicsCount(doc.Name)?.ToString() ?? "missing"}; definitionEntities={CustomGraphics.SnapshotEntityCount(doc)} (-1 means missing)\n");
        using var transaction = doc.Database.TransactionManager.StartTransaction();
        var table = (BlockTable)transaction.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
        foreach (var block in blocks)
            doc.Editor.WriteMessage($"  {block.Name} @ {block.Position}; definition={(table.Has(block.Name) ? "found" : "MISSING")}\n");
        var space = (BlockTableRecord)transaction.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForRead);
        foreach (ObjectId id in space)
        {
            if (id.IsErased || transaction.GetObject(id, OpenMode.ForRead, false) is not BlockReference reference) continue;
            var definitionId = reference.IsDynamicBlock ? reference.DynamicBlockTableRecord : reference.BlockTableRecord;
            var definition = (BlockTableRecord)transaction.GetObject(definitionId, OpenMode.ForRead);
            doc.Editor.WriteMessage($"  modelBlock={definition.Name} @ {reference.Position}\n");
        }
    }

    [CommandMethod("P3DAUTOISOBLOCKSYNC", CommandFlags.Modal)]
    public void SyncCommand()
    {
        var doc = AcApplication.DocumentManager.MdiActiveDocument;
        if (doc is null || !IsIsoTemplate(doc)) return;
        Sync(doc, allowEmpty: true, captureGraphics: true);
    }

    private static void OnCreated(object sender, DocumentCollectionEventArgs e) => Attach(e.Document);
    private static void OnDestroyed(object sender, DocumentCollectionEventArgs e)
    {
        Pending.Remove(e.Document);
        if (Trackers.Remove(e.Document, out var tracker)) tracker.Detach();
    }
    private static void Attach(Document doc)
    {
        if (!Trackers.ContainsKey(doc)) Trackers.Add(doc, new DocumentTracker(doc));
    }
    private static bool IsIsoTemplate(Document doc)
    {
        try
        {
            return string.Equals(Path.GetFileName(doc.Name), "Iso.dwt", StringComparison.OrdinalIgnoreCase)
                && File.Exists(Path.Combine(Path.GetDirectoryName(doc.Name)!, "IsoConfig.xml"));
        }
        catch { return false; }
    }
    private static void OnIdle(object? sender, EventArgs e)
    {
        if (_restoring) return;
        var doc = AcApplication.DocumentManager.MdiActiveDocument;
        if (doc is null) return;
        if (!Pending.TryGetValue(doc, out var attempts))
        {
            if (IsIsoTemplate(doc) && Trackers.TryGetValue(doc, out var tracker) &&
                (tracker.ReadyToSync || !File.Exists(BlockConfig.PathFor(doc.Name))) &&
                doc.Editor.IsQuiescent && string.IsNullOrEmpty(doc.CommandInProgress))
            {
                tracker.DelayRetry();
                if (Sync(doc)) tracker.Synchronized();
            }
            return;
        }
        if (!IsIsoTemplate(doc)) { Pending.Remove(doc); return; }
        // EraseAllObjs runs inside Project Setup; restore after that pass on Idle,
        // outside Autodesk's erase callback and its active database transaction.
        _restoring = true;
        try
        {
            if (!File.Exists(BlockConfig.PathFor(doc.Name)) &&
                Trackers.TryGetValue(doc, out var bootstrapTracker) && bootstrapTracker.Bootstrap is { } bootstrap)
            {
                BlockConfig.ReplaceFromDrawing(doc.Name, bootstrap);
                Log($"bootstrap captured: blocks={bootstrap.Count}; drawing={doc.Name}");
            }
            Restore(doc);
            Pending.Remove(doc);
        }
        catch (System.Exception ex)
        {
            if (attempts >= 3) Pending.Remove(doc);
            else Pending[doc] = attempts + 1;
            Log($"restore failed ({attempts + 1}/3): {ex.GetType().Name}: {ex.Message}");
        }
        finally { _restoring = false; }
    }
    private static void Restore(Document doc)
    {
        var blocks = BlockConfig.Load(doc.Name);
        using var docLock = doc.LockDocument();
        using var transaction = doc.Database.TransactionManager.StartTransaction();
        var table = (BlockTable)transaction.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
        var space = (BlockTableRecord)transaction.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
        var inserted = 0;
        foreach (var block in blocks)
        {
            if (!table.Has(block.Name))
            {
                Log($"definition missing: {block.Name}; drawing={doc.Name}");
                continue;
            }
            var definitionId = table[block.Name];
            var exists = space.Cast<ObjectId>().Any(id =>
            {
                if (id.IsErased) return false;
                var entity = transaction.GetObject(id, OpenMode.ForRead, false);
                return entity is BlockReference reference &&
                    (reference.BlockTableRecord == definitionId ||
                     (reference.IsDynamicBlock && reference.DynamicBlockTableRecord == definitionId)) &&
                    reference.Position.DistanceTo(block.Position) < 0.001;
            });
            if (exists) continue;
            using var reference = new BlockReference(block.Position, definitionId) { Rotation = block.Rotation };
            space.AppendEntity(reference);
            transaction.AddNewlyCreatedDBObject(reference, true);
            var definition = (BlockTableRecord)transaction.GetObject(definitionId, OpenMode.ForRead);
            foreach (ObjectId attributeId in definition)
            {
                if (transaction.GetObject(attributeId, OpenMode.ForRead) is not AttributeDefinition attributeDefinition || attributeDefinition.Constant) continue;
                using var attribute = new AttributeReference();
                attribute.SetAttributeFromBlock(attributeDefinition, reference.BlockTransform);
                var configured = block.Attributes?.FirstOrDefault(a => string.Equals(a.Tag, attributeDefinition.Tag, StringComparison.OrdinalIgnoreCase));
                attribute.TextString = configured?.Value ?? attributeDefinition.TextString;
                reference.AttributeCollection.AppendAttribute(attribute);
                transaction.AddNewlyCreatedDBObject(attribute, true);
            }
            inserted++;
            Log($"restored {block.Name} @ {block.Position}; drawing={doc.Name}");
        }
        transaction.Commit();
        if (inserted > 0) Log($"completed: inserted={inserted}; drawing={doc.Name}");
        var graphics = CustomGraphics.Restore(doc);
        Log($"custom graphics restored: entities={graphics}; drawing={doc.Name}");
    }
    private static bool Sync(Document doc, bool allowEmpty = false, bool captureGraphics = false)
    {
        try
        {
            if (Pending.ContainsKey(doc))
            {
                Log($"sync skipped: restore pending; drawing={doc.Name}");
                return false;
            }
            var actual = CaptureBlocks(doc);
            var graphics = captureGraphics ? CustomGraphics.Save(doc) : -1;
            // A transient empty model during Project Setup must not clear XML.
            // BeginSave passes allowEmpty=true: the user's saved state is authoritative.
            if (!allowEmpty && actual.Count == 0 && BlockConfig.Load(doc.Name).Count > 0)
            {
                if (captureGraphics) BlockConfig.ReplaceFromDrawing(doc.Name, BlockConfig.Load(doc.Name), graphics);
                Log($"sync skipped: no custom blocks visible; preserving previous XML; drawing={doc.Name}");
                if (captureGraphics) Log($"custom graphics saved: entities={graphics}; drawing={doc.Name}");
                return true;
            }
            var count = BlockConfig.ReplaceFromDrawing(doc.Name, actual, captureGraphics ? graphics : null);
            Log($"sync completed: blocks={count}; drawing={doc.Name}");
            if (captureGraphics) Log($"custom graphics saved: entities={graphics}; drawing={doc.Name}");
            doc.Editor.WriteMessage($"\nISO block config synchronized: {count} block(s).\n");
            return true;
        }
        catch (System.Exception ex)
        {
            Log($"sync failed: {ex.GetType().Name}: {ex.Message}; drawing={doc.Name}");
            return false;
        }
    }
    private static List<BlockSpec> CaptureBlocks(Document doc, BlockReference? erasedReference = null)
    {
        var titleBlock = BlockConfig.TitleBlockName(doc.Name);
        var northArrow = BlockConfig.NorthArrowName(doc.Name);
        if (string.IsNullOrWhiteSpace(titleBlock)) throw new InvalidDataException("TitleBlock Symbol missing in IsoConfig.xml.");
        var actual = new List<BlockSpec>();
        var previous = BlockConfig.Load(doc.Name).ToList();
        using var transaction = doc.Database.TransactionManager.StartTransaction();
        var table = (BlockTable)transaction.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
        var space = (BlockTableRecord)transaction.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForRead);
        void Add(BlockReference reference)
        {
            var definitionId = reference.IsDynamicBlock ? reference.DynamicBlockTableRecord : reference.BlockTableRecord;
            var definition = (BlockTableRecord)transaction.GetObject(definitionId, OpenMode.ForRead);
            if (string.Equals(definition.Name, titleBlock, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(definition.Name, northArrow, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(definition.Name, CustomGraphics.Name, StringComparison.OrdinalIgnoreCase) ||
                definition.Name.StartsWith('*') ||
                string.Equals(reference.Layer, "Table", StringComparison.OrdinalIgnoreCase)) return;
            var matching = previous.Where(b => string.Equals(b.Name, definition.Name, StringComparison.OrdinalIgnoreCase))
                .OrderBy(b => b.Position.DistanceTo(reference.Position)).FirstOrDefault();
            if (matching is not null) previous.Remove(matching);
            var attributes = new List<AttributeSpec>();
            if (reference.AttributeCollection is { } attributeCollection)
            foreach (ObjectId attributeId in attributeCollection)
            {
                if (attributeId.IsErased || transaction.GetObject(attributeId, OpenMode.ForRead, false) is not AttributeReference attribute) continue;
                var source = matching?.Attributes?.FirstOrDefault(a => string.Equals(a.Tag, attribute.Tag, StringComparison.OrdinalIgnoreCase))?.Source;
                attributes.Add(new AttributeSpec(attribute.Tag, attribute.TextString, source));
            }
            if (attributes.Count == 0 && matching?.Attributes is { Count: > 0 }) attributes.AddRange(matching.Attributes);
            actual.Add(new BlockSpec(definition.Name, reference.Position, reference.Rotation, attributes));
        }
        foreach (ObjectId id in space)
        {
            if (id.IsErased || transaction.GetObject(id, OpenMode.ForRead, false) is not BlockReference reference) continue;
            Add(reference);
        }
        if (erasedReference is not null && erasedReference.ObjectId.IsErased) Add(erasedReference);
        return actual;
    }
    private sealed class DocumentTracker
    {
        private readonly Document _document;
        private bool _dirty;
        private DateTime _nextSyncUtc;
        internal IReadOnlyList<BlockSpec>? Bootstrap { get; private set; }
        internal bool ReadyToSync => _dirty && DateTime.UtcNow >= _nextSyncUtc;
        internal void Synchronized() => _dirty = false;
        internal void DelayRetry() => _nextSyncUtc = DateTime.UtcNow.AddSeconds(3);
        private void MarkDirty()
        {
            if (_restoring || !IsIsoTemplate(_document)) return;
            _dirty = true;
            _nextSyncUtc = DateTime.UtcNow.AddMilliseconds(350);
        }
        internal DocumentTracker(Document doc)
        {
            _document = doc;
            doc.Database.ObjectErased += OnErased;
            doc.Database.ObjectModified += OnModified;
            doc.Database.ObjectAppended += OnAppended;
            doc.EndDwgOpen += OnEndDwgOpen;
            doc.CloseWillStart += OnCloseWillStart;
            doc.Database.BeginSave += OnBeginSave;
        }
        internal void Detach()
        {
            _document.Database.ObjectErased -= OnErased;
            _document.Database.ObjectModified -= OnModified;
            _document.Database.ObjectAppended -= OnAppended;
            _document.EndDwgOpen -= OnEndDwgOpen;
            _document.CloseWillStart -= OnCloseWillStart;
            _document.Database.BeginSave -= OnBeginSave;
        }
        private void OnEndDwgOpen(object? sender, EventArgs e)
        {
            if (IsIsoTemplate(_document) && !File.Exists(BlockConfig.PathFor(_document.Name)) && Sync(_document)) Synchronized();
        }
        private void OnModified(object sender, ObjectEventArgs e)
        {
            if (ShouldTrack(e.DBObject)) MarkDirty();
        }
        private void OnAppended(object sender, ObjectEventArgs e)
        {
            if (ShouldTrack(e.DBObject)) MarkDirty();
        }
        private bool ShouldTrack(DBObject obj) => obj is BlockReference or AttributeReference ||
            (obj is Entity entity && entity.OwnerId == _document.Database.CurrentSpaceId && CustomGraphics.IsCustomerGraphics(entity));
        private void OnBeginSave(object sender, DatabaseIOEventArgs e)
        {
            if (IsIsoTemplate(_document))
            {
                Log($"template save started: drawing={_document.Name}");
                // Saving an empty template records intentional deletion as an empty snapshot.
                Sync(_document, allowEmpty: true, captureGraphics: true);
            }
        }
        private void OnCloseWillStart(object? sender, EventArgs e)
        {
            if (IsIsoTemplate(_document)) Sync(_document);
        }
        private void OnErased(object sender, ObjectErasedEventArgs e)
        {
            try
            {
                if (_restoring || !e.Erased || !IsIsoTemplate(_document)) return;
                // Only the exact Autodesk setup pass observed in WinDbg may queue restoration.
                var frames = new StackTrace(false).GetFrames();
                if (frames is null || !frames.Any(frame =>
                    frame.GetMethod()?.DeclaringType?.FullName == "Autodesk.ProcessPower.PnP3dIsoUI.ISOUIUtils" &&
                    frame.GetMethod()?.Name == "EraseAllObjs"))
                {
                    if (ShouldTrack(e.DBObject)) MarkDirty();
                    return;
                }
                if (!File.Exists(BlockConfig.PathFor(_document.Name)) && Bootstrap is null)
                    Bootstrap = CaptureBlocks(_document, e.DBObject as BlockReference);
                Pending[_document] = 0;
                _dirty = false;
            }
            catch (System.Exception ex)
            {
                Log($"erase observer failed: {ex}");
            }
        }
    }
    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch { /* Logging must never interrupt AutoCAD. */ }
    }
}
