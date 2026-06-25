#!/usr/bin/env bash
# Build the standalone GX2 (Wii U Latte) shader -> GLSL decompiler (gx2dec.exe).
#
# gx2dec is a thin CLI driver (src/) over Cemu's legacy Latte shader decompiler.
# The Cemu decompiler sources are MPL-2.0 (https://github.com/cemu-project/Cemu,
# commit 079a4af). Place them under inc/ mirroring Cemu's src/ layout before
# building. See README.md for the exact file list and toolchain.
set -e

GX2DEC="${GX2DEC:-$(cd "$(dirname "$0")" && pwd)}"   # this script's directory
CXX="${CXX:-clang++}"                                # llvm-mingw clang++ (C++20)
FMT_INC="${FMT_INC:-$GX2DEC/fmt/include}"            # header-only {fmt}
BUILD="$GX2DEC/build"
mkdir -p "$BUILD"

INC="-I $GX2DEC/inc -I $FMT_INC"
FLAGS="-std=c++20 -O2 -DFMT_HEADER_ONLY -DENABLE_OPENGL $INC -include Common/precompiled.h"

# Cemu decompiler sources (MPL-2.0, Cemu commit 079a4af) + our driver (src/).
SRC=(
  "$GX2DEC/inc/Cafe/HW/Latte/LegacyShaderDecompiler/LatteDecompiler.cpp"
  "$GX2DEC/inc/Cafe/HW/Latte/LegacyShaderDecompiler/LatteDecompilerAnalyzer.cpp"
  "$GX2DEC/inc/Cafe/HW/Latte/LegacyShaderDecompiler/LatteDecompilerEmitGLSL.cpp"
  "$GX2DEC/inc/Cafe/HW/Latte/LegacyShaderDecompiler/LatteDecompilerEmitGLSLAttrDecoder.cpp"
  "$GX2DEC/inc/Cafe/HW/Latte/LegacyShaderDecompiler/LatteDecompilerRegisterDataTypeTracker.cpp"
  "$GX2DEC/inc/Cafe/HW/Latte/Core/FetchShader.cpp"
  "$GX2DEC/inc/Cafe/HW/Latte/Core/LatteGSCopyShaderParser.cpp"
  "$GX2DEC/src/globals.cpp"
  "$GX2DEC/src/latteshader_impl.cpp"
  "$GX2DEC/src/main.cpp"
)

OBJS=()
for f in "${SRC[@]}"; do
  obj="$BUILD/$(basename "${f%.cpp}").o"
  echo "[CXX] $(basename "$f")"
  "$CXX" $FLAGS -c "$f" -o "$obj"
  OBJS+=("$obj")
done

echo "[LINK] gx2dec.exe"
# static link so the .exe is self-contained (no libc++/UCRT DLLs needed on PATH)
"$CXX" $FLAGS -static -static-libgcc "${OBJS[@]}" -o "$GX2DEC/gx2dec.exe"
echo "Build complete: $GX2DEC/gx2dec.exe"
