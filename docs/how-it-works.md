# Plant Automation ISO Template Restore

This standalone add-in for AutoCAD Plant 3D 2027 preserves customer content in ISO templates. It applies to any style directory containing both `Iso.dwt` and `IsoConfig.xml`. Each style has its own `PlantAutomation.IsoBlocks.xml` **beside its `Iso.dwt`**. The add-in does not modify Autodesk DLLs or `IsoConfig.xml`.

## Why it is needed

When **Project Setup → Setup Title Block** opens, Plant rebuilds `Iso.dwt` and erases some independent entities. WinDbg captured this call path:

```text
PnP3dWorkspace.IsoAreaData.Initialize
  → PnP3dIsoUI.ISOUIUtils.ReadFromEngine
    → PnP3dIsoUI.ISOUIUtils.EraseAllObjs
      → Autodesk.AutoCAD.DatabaseServices.DBObject.Erase
```

The add-in recognizes erasures from `EraseAllObjs`. It does not edit the drawing inside the erase event handler. On the next `Application.Idle`, it restores customer blocks from XML in a separate transaction. It does not insert another reference if one with the same definition already exists at the same position. The block definition must already exist in `Iso.dwt`; the add-in does not import it from another file.

## Independent customer graphics

The add-in clones eligible AutoCAD drawing entities from model space into a `PA_ISO_CUSTOM_GRAPHICS` block definition with origin `(0,0,0)`. Block references, system tables, viewports, entities on layer `Table`, and entities on Plant's `Area-*` layers are excluded. Plant recreates the Drawing Area overlays from `IsoConfig.xml`; restoring them from the graphics snapshot can duplicate the areas. Older snapshots are also filtered when restored. Original customer entities remain separate and editable. Plant 3D custom entities from other assemblies are excluded. Customer block references use the XML mechanism instead.

The definition is replaced from current independent entities only when `Iso.dwt` is saved or `P3DAUTOISOBLOCKSYNC` is run. Ordinary XML synchronization does not replace it because Project Setup may temporarily leave only some entities in model space while rebuilding the template. The snapshot has no version history. After an erase, the add-in inserts `PA_ISO_CUSTOM_GRAPHICS` at `(0,0,0)`, explodes it, compares the resulting entities with those Plant retained, adds missing entities, and removes the block reference. Geometry is stored in `Iso.dwt`, not in XML.

Each save of `Iso.dwt` replaces the `PA_ISO_CUSTOM_GRAPHICS` definition with the current eligible model-space entities. Its entity count can increase, decrease, or become zero. The add-in does not infer whether a deletion was intentional; the user controls the saved template content. `P3DAUTOISOBLOCKSYNC` can write the snapshot without saving, but normal use does not require it.

XML records only snapshot metadata, for example `<CustomGraphics name="PA_ISO_CUSTOM_GRAPHICS" entityCount="147" />`. Ordinary synchronization of customer blocks preserves this entry. `P3DAUTOISOBLOCKSTATUS` reports the XML count and the actual count in the `Iso.dwt` definition to help detect a mismatch.

During restore, lines, polylines, circles, arcs, and text are compared using geometry and basic properties. Identical bounding extents alone are insufficient to match different polylines. Matching entities are counted individually so two exact overlapping copies can remain two copies. For other entity types or unreadable properties, the add-in restores the entity conservatively; this can produce a duplicate, but avoids omitting a missing entity.

This workflow assumes template edits are made in Plant 3D. The add-in does not observe changes to `Iso.dwt` made outside Plant. In the completed tests, the XML entity count matched the saved definition. Plant erased all customer graphics and the add-in restored all of them; partial retention during reconstruction was not observed in those runs.

## What is stored

After a customer block or its attribute changes, the add-in waits until the command completes and AutoCAD is idle. It then writes the block name, position, rotation, and **attribute text values** from model space to XML. It also synchronizes when `Iso.dwt` actually closes. This works after **Return to Project Setup**, which does not necessarily close the drawing.

The add-in excludes the block named by `TitleBlock Symbol` in `IsoConfig.xml`, the block named by `NorthArrow Symbol`, anonymous blocks, block references on layer `Table`, and Plant system tables. Multiple references to one customer block are stored as separate entries. Outside a save, ordinary synchronization retains a previous nonempty block list if no customer block is temporarily visible. **Saving `Iso.dwt` records the actual state even when the customer block list is empty.** If the user deletes all customer content and saves, the add-in restores none of it. Plant manages its own system elements.

If the XML does not exist yet, the add-in creates it for the style when the template opens. During the first Setup erase, it attempts to capture the original blocks before restoration. XML read and restoration errors are logged.

## Attributes and `source`

On restore, the add-in creates `AttributeReference` objects from the block's attribute definitions. It saves ordinary text as `value`. If an attribute contains an AutoCAD FIELD, it also saves the expression as `fieldCode` and attaches a new FIELD to the restored reference. This preserves dynamic values such as a plot date. A FIELD already lost from both the XML and the block definition must be inserted once again in the template. The optional `source` field is preserved as metadata; **it does not yet fetch values from the project database**. Other reference properties and individual formatting are not guaranteed to be reproduced.

Example:

```xml
<IsoBlocks>
  <Block name="CUSTOM_NOTE" x="309" y="297" z="0" rotation="0">
    <Attributes>
      <Attribute tag="PROJECT_ID" value="EXAMPLE-001" source="PROJECT-IDENTIFIER" />
      <Attribute tag="REVISION" value="0" />
    </Attributes>
  </Block>
</IsoBlocks>
```

Here, `source` is an optional mapping added manually; automatic capture writes `tag` and `value`. Coordinates use a decimal point, and rotation is in radians.

## Verified behavior

- In one ISO style, a customer block reference remained at its position after restoration; subsequent synchronization recorded it in XML. The anonymous table and `NorthArrow` were excluded.
- In another style, a separate customer block was recorded in XML beside its `Iso.dwt`. Revised attribute text was recorded and preserved through restoration.
- An older version protected a `2/2` snapshot in an isolated template copy when only one or zero entities were visible. That rule was removed to honor the saved state. The user subsequently tested the current behavior for deleted graphics in Plant 3D.
- In an open ISO style, 6 entities were saved. After Return to Project Setup and reopening Setup Title Block, the log reported `custom graphics restored: entities=6`, and status remained `6/6`.
- After exact overlapping copies were added, the snapshot grew from 6 to 9 entities. On reopening, the log reported restoration of all 9, status was `9/9`, and the user confirmed that both overlapping copies remained.

These results cover two styles and one isolated template copy. In the last test, Plant erased all 9 entities, so the geometry key was not tested against a case where Plant retained one similar entity but erased another. `xmlEntities` and `definitionEntities` count the saved snapshot; they do not directly count visible model-space entities. Other styles use the same `Iso.dwt` and adjacent `IsoConfig.xml` detection, but their individual templates were not tested.

## Build and operation

The source is in `src/PlantAutomation.IsoTemplateRestore/`. `Build.ps1` creates a bundle under `tmp/`. With Plant 3D 2027 closed, `Install.ps1` installs it to `%APPDATA%\Autodesk\ApplicationPlugins\PlantAutomationIsoTemplateRestore.bundle`. Use `Install.ps1 -Upgrade` to replace the deployed DLL; it backs up the previous DLL under `tmp/backup`. The installer does not overwrite per-style XML files.

```powershell
powershell -ExecutionPolicy Bypass -File .\Build.ps1
powershell -ExecutionPolicy Bypass -File .\Install.ps1 -Upgrade
```

In an open `Iso.dwt`, `P3DAUTOISOBLOCKSTATUS` reports configured blocks and whether their definitions exist. `P3DAUTOISOBLOCKSYNC` forces the current state to be written, including an empty block list. The log is at `%LOCALAPPDATA%\PlantAutomation\iso-template-restore.log`.

Uninstall with `Install.ps1 -Uninstall`. It removes the bundle but retains each style's XML file.
