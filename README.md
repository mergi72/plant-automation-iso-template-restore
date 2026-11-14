# Plant Automation ISO Template Restore

License: [MIT](LICENSE).

This add-in for AutoCAD Plant 3D 2027 preserves customer blocks in `Iso.dwt` that **Setup Title Block** removes when it rebuilds the template. It works with any ISO style that has `IsoConfig.xml` beside `Iso.dwt`. Each style has its own `PlantAutomation.IsoBlocks.xml` in the same directory.

When a customer block or one of its attributes changes, the add-in records its position, rotation, and attribute text in XML. AutoCAD FIELD expressions in attributes are also stored and restored. It restores the blocks after `ISOUIUtils.EraseAllObjs` runs. It excludes the title block, NorthArrow, and Plant system tables. The optional `source` attribute is retained as metadata; it does not yet read project data through that mapping.

The add-in stores separate AutoCAD drawing entities in a `PA_ISO_CUSTOM_GRAPHICS` block definition inside `Iso.dwt` when the template is saved or `P3DAUTOISOBLOCKSYNC` is run. Plant's `Area-*` layers are excluded because Project Setup recreates Drawing Area overlays itself. The source customer entities remain separate and editable in model space. After Plant rebuilds the template, the add-in inserts the definition at `(0,0,0)`, explodes it, and restores only missing entities. Customer block references are managed through XML. XML also records the definition name and entity count, but contains no geometry.

Every save of `Iso.dwt` records the current customer blocks in XML and replaces the graphics snapshot with the current separate model-space entities. Additions and deletions are saved automatically. If the user deletes all customer content and saves, the add-in restores none of it. Plant 3D remains responsible for its system elements. No manual command is needed for normal use.

In one ISO style, reopening Setup Title Block restored all 6 entities. After overlapping copies were added, 9 entities were saved and restored. The user also tested the current save behavior for deleted graphics in Plant 3D.

The add-in was also checked with two ISO styles: it restored a customer block at its original position and retained revised attribute text. See [how-it-works.md](docs/how-it-works.md) for the mechanism, evidence, and limitations.

## Build and install

Close Plant 3D 2027 before installing.

```powershell
powershell -ExecutionPolicy Bypass -File .\Build.ps1
powershell -ExecutionPolicy Bypass -File .\Install.ps1
```

For an upgrade, run `Install.ps1 -Upgrade`; the previous DLL is backed up. In an open `Iso.dwt`, use `P3DAUTOISOBLOCKSTATUS` to inspect the state or `P3DAUTOISOBLOCKSYNC` to write the current state manually. The log is at `%LOCALAPPDATA%\PlantAutomation\iso-template-restore.log`.
