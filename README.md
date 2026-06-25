# Switch-Toolbox: Breath of the Wild Effects

A fork of [KillzXGaming's Switch-Toolbox](https://github.com/KillzXGaming/Switch-Toolbox), a tool for editing
Nintendo Switch, 3DS and Wii U formats. This fork adds rendering and editing for *The Legend of Zelda:
Breath of the Wild* particle effects, the Wii U `.sesetlist` (EFTB) emitter sets.

The upstream project is archived. This fork keeps the base toolbox intact and adds BotW effect support on top
of it.

## What this fork adds

Breath of the Wild stores its visual effects (fire, smoke, sparks, water ripples, Guardian beams) as
`nw::eft` emitter sets inside `.sesetlist` files. Stock Switch-Toolbox parses the surrounding PTCL container
but cannot render or meaningfully edit these Wii U effects. This fork makes them viewable, simulated, and
editable from the file's own bytes.

### Rendering

Selecting an emitter in a `.sesetlist` renders it in real time, simulated on the CPU from decoded fields:
lifespan, emit rate, two-stage emission velocity, dispersion cone, air resistance, volume shapes, and
momentum randomization.

- Particle types: camera-facing billboard quads, PRIM mesh particles for rings, shockwaves and ripples, and
  connection and trail stripes drawn as tapered ribbons.
- Shading: color, alpha and scale 8-key curves over life; a 3-texture combiner with per-slot blend ops
  (multiply, add, subtract, max); blend mode, depth test, face culling and alpha test; per-slot flipbook
  atlases; per-axis texture wrap with quadrant mirror-tiling.
- A refraction pass: emitters that resample the scene buffer, such as water ripples and heat distortion,
  warp the rendered background behind them.
- Automatic camera framing to the effect's extent.

### Editing

- A Parameters editor with live preview: color, alpha and scale curves, texture-sampler settings (wrap,
  UV-expand, max-aniso), per-slot flipbook grid fields, and emitter shader re-pointing.
- Emitter Set, Texture and Primitive editing: add, delete, clear and duplicate. Deleting a texture or
  primitive lists the emitters that still reference it.
- Import and export of single emitters (`.eftemitter`) and whole sets (`.eftset`) between `.sesetlist`
  files. The bundles carry the referenced textures, primitives and GX2 shaders so a moved emitter still
  renders.
- OBJ import and export of PRIM meshes.

### Shaders

- View an emitter's bound GX2 (Wii U Latte) vertex and fragment shaders decompiled to GLSL, using a
  Cemu-derived decompiler bundled as `gx2dec.exe`.
- A shader-usage view that maps each shader to the emitters and files that bind it, plus prune and clear
  tools for unused shaders.

The renderer is driven from reverse-engineered `nw::eft` field offsets. [`CHANGELOG.md`](CHANGELOG.md)
records each offset, its meaning, and how it was verified.

## The base tool

Everything the original tool does still works. It edits and previews many Nintendo formats, including BFRES,
BNTX, SARC, BYAML, AAMP, KCL, PTCL and EFC effects, BFLYT and audio. The
[upstream README](https://github.com/KillzXGaming/Switch-Toolbox) has the full list and tutorials.

## Building

A .NET Framework WinForms solution. Open `Toolbox.sln` in Visual Studio (2017 or newer) and build as Release,
or build with MSBuild. NuGet packages restore from `packages/`.

If the build fails, check the project references first. Visual Studio may block source files downloaded from
the web; from the project root run `Get-ChildItem -Path "C:\Full\Path\To\Folder" -Recurse | Unblock-File`.
The build copies `gx2dec.exe` and `gx2dec.NOTICE.txt` next to the output.

## Credits

This fork is built on [Switch-Toolbox by KillzXGaming](https://github.com/KillzXGaming/Switch-Toolbox) and
its contributors; the upstream README lists the full credits.

BotW effect support also uses:
- [Cemu](https://github.com/cemu-project/Cemu). The `gx2dec` shader decompiler (GX2 Wii U Latte to GLSL) is
  derived from Cemu's MPL-2.0 Latte decompiler, commit `079a4af`. Source and license are in
  `File_Format_Library/gx2dec_src/` and `File_Format_Library/gx2dec.NOTICE.txt`.
- [NW4F-Eft (open-ead)](https://github.com/open-ead/NW4F-Eft) and RenderDoc captures, used to
  reverse-engineer the `nw::eft` simulation and field offsets.

## License

See `LICENSE` for the base toolbox. The bundled `gx2dec` decompiler is MPL-2.0, with its NOTICE included next
to the binary. Let me know if a library should not be used or if a credit is missing.
