#!/usr/bin/env bash
# Şablon paketi (CanFx.Templates): verilen sürümle paketler. Şablonun kullandığı CanFx sürümü de bu sürüm olur, yani
# şablonla oluşturulan proje birlikte yayınlanan paketleri kullanır.
#
#   build/pack-templates.sh <sürüm> <çıktı klasörü>
#
# Çağıranlar: build/pack-local.sh (yerel sürüm) ve build/ci.sh pack (CI / yayın sürümü). Yalnızca bash + sed: CI
# imajlarında python gerekmez.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSION="${1:?Kullanım: build/pack-templates.sh <sürüm> <çıktı klasörü>}"
OUT="${2:?Kullanım: build/pack-templates.sh <sürüm> <çıktı klasörü>}"
PREFIX="$(sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' "$ROOT/Directory.Build.props" | head -n 1)"
STAGE="$ROOT/artifacts/obj/templates-stage"

rm -rf "$STAGE" && mkdir -p "$STAGE" "$OUT"
cp -R "$ROOT/templates/CanFx.Templates/." "$STAGE/"

# Kopya, kökün Directory.Build.props'unu bulamaz: sürüm öneki elle verilir.
cat > "$STAGE/Directory.Build.props" <<PROPS
<Project>
  <PropertyGroup>
    <VersionPrefix>$PREFIX</VersionPrefix>
  </PropertyGroup>
</Project>
PROPS
sed -i.bak "s#<VersionPrefix>\$(\[System.Text.RegularExpressions.Regex\].*</VersionPrefix>##" "$STAGE/CanFx.Templates.csproj"

# TEMPLATE_PACKAGE_VERSION: template.json'daki CanFxVersion varsayılanı ve Directory.Packages.props'taki not
grep -rl "TEMPLATE_PACKAGE_VERSION" "$STAGE/content" | while IFS= read -r file; do
  sed -i.bak "s#TEMPLATE_PACKAGE_VERSION#$VERSION#g" "$file"
done
find "$STAGE" -name "*.bak" -delete

dotnet pack "$STAGE/CanFx.Templates.csproj" -c Release -o "$OUT" -p:Version="$VERSION"
