#!/usr/bin/env bash
# Yerel paketleme: Can.Core paketleri, can-gen aracı ve proje şablonu AYNI yerel sürümle paketlenir.
#
#   build/pack-local.sh             → artifacts/packages (Core + can-gen), artifacts/templates (şablon)
#   build/pack-local.sh --install   → ayrıca şablonu (dotnet new) ve can-gen'i (dotnet tool) bu sürümle kurar
#
# Sürüm: <VersionPrefix>-local.<yyyyMMddHHmm> (ör. 0.1.0-local.202610171530). Her paketleme yeni bir sürümdür; NuGet
# önbelleğini silmek ya da "--force" ile üzerine yazmak gerekmez. Sabit sürüm için: VERSION=0.1.0-local.1 build/pack-local.sh
# Şablonun varsayılan Can.Core sürümü (CanCoreVersion) bu sürüme ayarlanır: şablonla oluşturulan proje, birlikte
# paketlendiği Core paketlerini kullanır.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PREFIX="$(sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' "$ROOT/Directory.Build.props" | head -n 1)"
VERSION="${VERSION:-$PREFIX-local.$(date +%Y%m%d%H%M)}"
PACKAGES="$ROOT/artifacts/packages"
TEMPLATES="$ROOT/artifacts/templates"
STAGE="$ROOT/artifacts/obj/templates-stage"
INSTALL=false
[[ "${1:-}" == "--install" ]] && INSTALL=true

echo "==> Sürüm: $VERSION"

echo "==> Can.Core paketleri ve can-gen"
dotnet pack "$ROOT/Core.slnx" -c Release -o "$PACKAGES" -p:Version="$VERSION"

echo "==> Şablon paketi"
rm -rf "$STAGE" && mkdir -p "$STAGE" "$TEMPLATES"
cp -R "$ROOT/templates/Can.Core.Templates/." "$STAGE/"
# Kopya kökün Directory.Build.props'unu bulamaz: sürüm öneki elle verilir.
cat > "$STAGE/Directory.Build.props" <<PROPS
<Project>
  <PropertyGroup>
    <VersionPrefix>$PREFIX</VersionPrefix>
  </PropertyGroup>
</Project>
PROPS
sed -i.bak "s#<VersionPrefix>\$(\[System.Text.RegularExpressions.Regex\].*</VersionPrefix>##" "$STAGE/Can.Core.Templates.csproj" && rm -f "$STAGE/Can.Core.Templates.csproj.bak"
for json in "$STAGE"/content/*/.template.config/template.json; do
  python3 - "$json" "$VERSION" <<'PY'
import json, sys
path, version = sys.argv[1], sys.argv[2]
data = json.load(open(path, encoding="utf-8"))
data["symbols"]["CanCoreVersion"]["defaultValue"] = version
json.dump(data, open(path, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY
done
for props in "$STAGE"/content/*/Directory.Packages.props; do
  sed -i.bak "s#TEMPLATE_PACKAGE_VERSION#$VERSION#" "$props" && rm -f "$props.bak"
done
dotnet pack "$STAGE/Can.Core.Templates.csproj" -o "$TEMPLATES" -p:Version="$VERSION"

echo "$VERSION" > "$ROOT/artifacts/local-version.txt"

# Eski yerel sürümler: son $KEEP sürüm tutulur (disk dolmasın; eski sürümle oluşturulmuş projeler bir süre derlenmeye devam etsin).
KEEP="${KEEP:-3}"
python3 - "$PACKAGES" "$TEMPLATES" "$HOME/.nuget/packages" "$KEEP" <<'PY'
import os, re, shutil, sys
packages, templates, cache, keep = sys.argv[1], sys.argv[2], sys.argv[3], int(sys.argv[4])
stamp = re.compile(r"-local\.(\d{12})")
def old_stamps(names):
    found = sorted({m.group(1) for n in names if (m := stamp.search(n))})
    return set(found[:-keep]) if len(found) > keep else set()
for folder in (packages, templates):
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
        if not package.startswith("can.core"):
            continue
        root = os.path.join(cache, package)
        versions = os.listdir(root)
        drop = old_stamps(versions)
        for v in versions:
            m = stamp.search(v)
            if (m and m.group(1) in drop) or v.endswith("-local"):
                shutil.rmtree(os.path.join(root, v), ignore_errors=True)
PY

if $INSTALL; then
  echo "==> Kurulum"
  # Aynı paket kimliğinin her sürümü dotnet new'de AYRI kurulum olarak kalır ve çakışır (ilki kullanılır): hepsini kaldır.
  for _ in $(seq 1 20); do
    dotnet new uninstall Can.Core.Templates >/dev/null 2>&1 || break
  done
  dotnet new install "$TEMPLATES/Can.Core.Templates.$VERSION.nupkg"
  dotnet tool update -g Can.Core.Generator.Cli --add-source "$PACKAGES" --version "$VERSION" \
    || dotnet tool install -g Can.Core.Generator.Cli --add-source "$PACKAGES" --version "$VERSION"
fi

echo "==> Tamam: $VERSION"
$INSTALL && echo "    Rider açıksa yeni şablonu görmesi için kapatıp aç (⌘Q)."
echo "    Core + can-gen: $PACKAGES"
echo "    Şablon:         $TEMPLATES/Can.Core.Templates.$VERSION.nupkg"
