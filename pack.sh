#!/usr/bin/env bash
# 生成免安装 .NET 的单文件自包含程序并打包为 zip（便于分发）
# 用法：./pack.sh
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DOTNET="${DOTNET:-/tmp/opencode/dotnet/dotnet}"
PROJ="$ROOT/app/desktop-pet-ui.csproj"
OUT="$ROOT/dist/desktop-pet-ui-win-x64"
ZIP="$ROOT/dist/desktop-pet-ui-win-x64.zip"

rm -rf "$OUT" "$ZIP"

echo "==> publish (win-x64 self-contained single-file)"
"$DOTNET" publish "$PROJ" -c Release -p:PublishProfile=win-x64-standalone

echo "==> assemble"
mkdir -p "$OUT"
cp -f "$ROOT/app/bin/Release/publish/standalone/desktop-pet-ui.exe" "$OUT/"
cp -ru "$ROOT/character/." "$OUT/character/"

echo "==> zip"
python3 - "$OUT" "$ZIP" <<'PY'
import sys, zipfile, os
src, dst = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(dst, "w", zipfile.ZIP_DEFLATED) as z:
    for root, dirs, files in os.walk(src):
        for f in files:
            p = os.path.join(root, f)
            z.write(p, os.path.relpath(p, src))
PY

echo "==> done"
ls -lh "$ZIP"
