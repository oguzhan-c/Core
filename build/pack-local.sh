#!/usr/bin/env bash
# Yerel paketleme: CanFx paketleri yeni bir yerel sürümle artifacts/packages'a.
#
#   build/pack-local.sh
#
# Sürüm: <VersionPrefix>-local.<yyyyMMddHHmm> (ör. 0.1.1-local.202610211530). Her paketleme yeni bir sürümdür; NuGet
# önbelleğini silmek gerekmez. Sabit sürüm için: VERSION=0.1.1-local.1 build/pack-local.sh
# Yayınlanmamış paketleri şablon ve can-gen ile denemek (Generator reposu):
#   CANFX_VERSION=$(cat ../Core/artifacts/local-version.txt) CANFX_FEED=$PWD/../Core/artifacts/packages templates/verify-templates.sh
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PREFIX="$(sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' "$ROOT/Directory.Build.props" | head -n 1)"
VERSION="${VERSION:-$PREFIX-local.$(date +%Y%m%d%H%M)}"
PACKAGES="$ROOT/artifacts/packages"

echo "==> Sürüm: $VERSION"

echo "==> CanFx paketleri"
dotnet pack "$ROOT/Core.slnx" -c Release -o "$PACKAGES" -p:Version="$VERSION"

echo "$VERSION" > "$ROOT/artifacts/local-version.txt"

# Eski yerel sürümler: son $KEEP sürüm tutulur (disk dolmasın; eski sürümle oluşturulmuş projeler bir süre derlenmeye devam etsin).
KEEP="${KEEP:-3}"
python3 - "$PACKAGES" "$HOME/.nuget/packages" "$KEEP" <<'PY'
import os, re, shutil, sys
packages, cache, keep = sys.argv[1], sys.argv[2], int(sys.argv[3])
stamp = re.compile(r"-local\.(\d{12})")
def old_stamps(names):
    found = sorted({m.group(1) for n in names if (m := stamp.search(n))})
    return set(found[:-keep]) if len(found) > keep else set()
for folder in (packages,):
    if not os.path.isdir(folder):
        continue
    names = os.listdir(folder)
    drop = old_stamps(names)
    for n in names:
        m = stamp.search(n)
        # zaman damgasız eski yerel paket (ör. ...0.1.0-local.nupkg) de karışıklık yaratır
        if (m and m.group(1) in drop) or re.search(r"-local\.s?nupkg$", n):
            os.remove(os.path.join(folder, n))
if os.path.isdir(cache):
    for package in os.listdir(cache):
        if package.startswith("can.core."):
            # eski adın (Can.Core.*) yerel paketleri: artık kullanılmıyor
            shutil.rmtree(os.path.join(cache, package), ignore_errors=True)
            continue
        if not package.startswith("canfx"):
            continue
        root = os.path.join(cache, package)
        versions = os.listdir(root)
        drop = old_stamps(versions)
        for v in versions:
            m = stamp.search(v)
            if (m and m.group(1) in drop) or v.endswith("-local"):
                shutil.rmtree(os.path.join(root, v), ignore_errors=True)
PY

echo "==> Tamam: $VERSION"
echo "    Paketler: $PACKAGES"
