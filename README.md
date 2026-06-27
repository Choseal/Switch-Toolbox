# Switch-Toolbox: Breath of the Wild Edition

A fork of [KillzXGaming's Switch-Toolbox](https://github.com/KillzXGaming/Switch-Toolbox), a tool for editing
Nintendo Switch, 3DS and Wii U formats. This fork adds first-class editing for *The Legend of Zelda: Breath of
the Wild*'s effect systems: the particle **emitter sets** (`.sesetlist` / EFTB) and the **effect-link database**
(ELink2 / `.belnk`) that drives them.

The upstream project is archived. This fork keeps the base toolbox intact and adds BotW support on top of it.

This fork's BotW additions were built tool-assisted with [Claude Code](https://www.anthropic.com/claude-code).
The reverse-engineering, simulation and file-editing code were developed with AI assistance, and every byte-level
finding was verified against the real game data (RenderDoc captures, the `nw::eft` and xlink2 layouts, and
round-trip re-parsing) before it shipped. [`CHANGELOG.md`](CHANGELOG.md) records each offset, its meaning, and
how it was confirmed.

## What this fork adds

BotW stores its visual effects (fire, smoke, sparks, water ripples, Guardian beams) as `nw::eft` emitter sets in
`.sesetlist` files, and it plays those sets *through* a second system, the ELink2 effect-link database, which
triggers them and overrides their parameters at runtime. Stock Switch-Toolbox parses the surrounding PTCL
container but cannot render or meaningfully edit either system. This fork makes both viewable, simulated and
editable from the file's own bytes.

### Effects: emitter sets (`.sesetlist` / EFTB)

**Rendering.** Selecting an emitter renders it in real time, simulated on the CPU from decoded fields: lifespan,
emit rate, two-stage emission velocity, dispersion cone, air resistance, volume shapes, and momentum randomization.

- Particle types: camera-facing billboard quads, PRIM mesh particles for rings, shockwaves and ripples, and
  connection and trail stripes drawn as tapered ribbons.
- Shading: color, alpha and scale 8-key curves over life; a 3-texture combiner with per-slot blend ops
  (multiply, add, subtract, max); blend mode, depth test, face culling and alpha test; per-slot flipbook
  atlases; per-axis texture wrap with quadrant mirror-tiling.
- A refraction pass: emitters that resample the scene buffer, such as water ripples and heat distortion, warp
  the rendered background behind them.
- Automatic camera framing to the effect's extent.

**Editing.**

- A Parameters editor with live preview: color, alpha and scale curves, texture-sampler settings (wrap,
  UV-expand, max-aniso), per-slot flipbook grid fields, and emitter shader re-pointing.
- Emitter Set, Texture and Primitive editing: add, delete, clear and duplicate. Deleting a texture or primitive
  lists the emitters that still reference it.
- Import and export of single emitters (`.eftemitter`) and whole sets (`.eftset`) between `.sesetlist` files.
  The bundles carry the referenced textures, primitives and GX2 shaders so a moved emitter still renders.
- OBJ import and export of PRIM meshes.

**Shaders.**

- View an emitter's bound GX2 (Wii U Latte) vertex and fragment shaders decompiled to GLSL, using a Cemu-derived
  decompiler bundled as `gx2dec.exe`.
- A shader-usage view that maps each shader to the emitters and files that bind it, plus prune and clear tools
  for unused shaders.

### Effect links (ELink2 / `.belnk`)

A viewer and editor for `Bootup.pack/ELink2/ELink2DB.belnk`, the effect-link database (xlink2, big-endian). The
game does not play emitter sets directly; it plays them through ELink, which can override their parameters and
gate them on runtime state. This fork exposes and edits that second data source alongside the `.sesetlist` editor.

**Viewer.**

- An ELink2DB tree: one folder per effect "user" (the actor or system that triggers effects), its asset-call
  tree (Switch / Random / Blend / Sequence containers show their watched property and branch condition inline),
  and an Actions / Triggers list. User blocks build lazily, since the file holds over 1300 users.
- Open the original emitter set: an asset links to the set it plays, and clicking it selects that set in any open
  `.sesetlist`.
- In-panel preview with the ELink overrides applied (scale, life, velocity, emission, color and alpha), so the
  preview matches how the game plays the effect rather than the raw set.

**Editing.**

- Edit, add and remove the parameter overrides on an asset through a PropertyGrid, isolated per asset
  (copy-on-write, so an edit never disturbs the other assets that share a block). Float overrides expand to
  constant or random ranges; curve-driven overrides open a points editor.
- Create, delete, duplicate and rename whole actors (the user entries that own an effect setup), with names
  persisted across save and reload.
- Create, delete and duplicate the call nodes that build an effect, including the trigger that fires each one.
- Edit the triggers and the conditions that gate an effect: action, property and always triggers, and Switch /
  Random / Sequence group conditions, with watched-property and value dropdowns drawn from the names the file
  already uses, glossed with English alongside the game's built-in Japanese names.

Every ELink edit is prototyped and verified against the real file (each re-parses clean across all users) before
it ships.

## The base tool

Everything the original tool does still works. It edits and previews many Nintendo formats, including BFRES,
BNTX, SARC, BYAML, AAMP, KCL, PTCL and EFC effects, BFLYT and audio. The
[upstream README](https://github.com/KillzXGaming/Switch-Toolbox) has the full list and tutorials.

## Building

A .NET Framework WinForms solution. Open `Toolbox.sln` in Visual Studio (2017 or newer) and build as Release, or
build with MSBuild. NuGet packages restore from `packages/`.

If the build fails, check the project references first. Visual Studio may block source files downloaded from the
web; from the project root run `Get-ChildItem -Path "C:\Full\Path\To\Folder" -Recurse | Unblock-File`. The build
copies `gx2dec.exe` and `gx2dec.NOTICE.txt` next to the output.

## Credits

This fork is built on [Switch-Toolbox by KillzXGaming](https://github.com/KillzXGaming/Switch-Toolbox) and its
contributors; the upstream README lists the full credits.

BotW support also uses:
- [Cemu](https://github.com/cemu-project/Cemu). The `gx2dec` shader decompiler (GX2 Wii U Latte to GLSL) is
  derived from Cemu's MPL-2.0 Latte decompiler, commit `079a4af`. Source and license are in
  `File_Format_Library/gx2dec_src/` and `File_Format_Library/gx2dec.NOTICE.txt`.
- [NW4F-Eft (open-ead)](https://github.com/open-ead/NW4F-Eft) and RenderDoc captures, used to reverse-engineer
  the `nw::eft` simulation and field offsets.

## License

See `LICENSE` for the base toolbox. The bundled `gx2dec` decompiler is MPL-2.0, with its NOTICE included next to
the binary. Let me know if a library should not be used or if a credit is missing.
