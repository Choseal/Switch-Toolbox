# gx2dec: GX2 (Wii U Latte) shader to GLSL decompiler

`gx2dec.exe` is the standalone tool the toolbox shells out to when viewing an emitter's
GX2 vertex/pixel shaders as GLSL (see `../FileFormats/Effects/Gx2ShaderDecompiler.cs`).
It is not linked into the toolbox; it runs as a separate process.

## What's ours vs Cemu's

- `src/main.cpp`, `src/latteshader_impl.cpp`, `src/globals.cpp`: the CLI driver (this project).
  It reads raw shader microcode + context registers from files and calls the decompiler.
- The decompiler itself is **Cemu's legacy Latte shader decompiler**, licensed under the
  **Mozilla Public License 2.0**:
  - Source: https://github.com/cemu-project/Cemu, commit `079a4af`
  - License: https://www.mozilla.org/MPL/2.0/

## Building

The build links Cemu's MPL-2.0 sources directly, so it is a C++ (clang++ / llvm-mingw)
build, not part of the C# `Toolbox.sln`. To reproduce `gx2dec.exe`:

1. Check out Cemu at commit `079a4af`.
2. Under `inc/`, mirror Cemu's `src/` layout and provide the files listed in `build.sh`
   (the `LatteDecompiler*`, `FetchShader`, `LatteGSCopyShaderParser` sources and their
   headers), plus Cemu's `Common/precompiled.h`.
3. Provide header-only [{fmt}](https://github.com/fmtlib/fmt) under `fmt/include` (or set
   `FMT_INC`).
4. Run `./build.sh` (override `CXX`, `GX2DEC`, `FMT_INC` via environment as needed).

A prebuilt `gx2dec.exe` is bundled at `../gx2dec.exe` for convenience; see
`../gx2dec.NOTICE.txt`.

## Usage

```
gx2dec <vs|ps> <program.bin> <regs.bin> [fetch.bin]
```

- `program.bin`: raw shader microcode bytes
- `regs.bin`: raw Latte context-register bytes (loaded into a zero-padded register array)
- `fetch.bin`: (vs only) raw fetch-shader microcode bytes

GLSL is written to stdout; the host binding/uniform map is written to stderr.
