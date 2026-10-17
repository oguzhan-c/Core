#!/usr/bin/env bash
# Şablonları yerelde doğrular: Can.Core paketlerini ve şablon paketini oluşturur, şablonu kurar, farklı seçeneklerle
# üç çözüm üretir ve her birini derler. Çıktı: <repo>/templates.log
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PACKAGES="$ROOT/artifacts/packages"
TEMPLATES="$ROOT/artifacts/templates"
OUT="$ROOT/artifacts/template-test"
LOG="$ROOT/templates.log"
VERSION="0.1.0-local"

: > "$LOG"
step() { echo "=== $*" | tee -a "$LOG"; }

# Can.Core kodu değişmediyse paketlemeyi atla: SKIP_CORE=1 ./templates/verify-templates.sh
if [[ "${SKIP_CORE:-0}" != 1 ]]; then
  step "Can.Core paketleri ($VERSION)"
  dotnet pack "$ROOT/Core.slnx" -c Release -o "$PACKAGES" -p:Version="$VERSION" >>"$LOG" 2>&1 || { step "HATA: Can.Core paketlenemedi"; exit 1; }
  # Aynı sürüm yeniden paketlendi: NuGet önbelleğindeki eski kopyalar silinir.
  rm -rf "$HOME"/.nuget/packages/can.core.*
fi

step "Şablon paketi"
rm -rf "$TEMPLATES" && mkdir -p "$TEMPLATES"
dotnet pack "$ROOT/templates/Can.Core.Templates/Can.Core.Templates.csproj" -o "$TEMPLATES" >>"$LOG" 2>&1 || { step "HATA: şablon paketlenemedi"; exit 1; }
dotnet new uninstall Can.Core.Templates >>"$LOG" 2>&1 || true
dotnet new install "$TEMPLATES/Can.Core.Templates.$VERSION.nupkg" >>"$LOG" 2>&1 || { step "HATA: şablon kurulamadı"; exit 1; }

rm -rf "$OUT" && mkdir -p "$OUT"
failed=0

variant() {
  local name="$1"; shift
  step "Çözüm: $name ($*)"
  if ! dotnet new can-monolith -n "$name" -o "$OUT/$name" --CanCoreFeed "$PACKAGES" "$@" >>"$LOG" 2>&1; then
    step "HATA: $name üretilemedi"; failed=1; return
  fi
  if dotnet build "$OUT/$name/$name.slnx" -c Debug >>"$LOG" 2>&1; then
    step "TAMAM: $name derlendi"
  else
    step "HATA: $name derlenemedi"; failed=1
  fi
  # Kalan TODO sayısı (bilgi)
  echo "    TODO(can-gen): $(grep -r --include=*.cs --include=*.json --include=*.config 'TODO(can-gen)' "$OUT/$name" | wc -l | tr -d ' ')" | tee -a "$LOG"
}

variant Minimal --Database sqlite --Otp false --EmailTwoFactor false --Passkey false --AuditTrail false
variant Full --Database postgresql --MultiTenancy true --ExternalLogin true --Redis true --EventBus true --Hangfire true \
  --FileStorage true --Search true --Realtime true --Sms true --Settings true --Reporting true --OpenTelemetry true
variant Tenant --Database sqlserver --MultiTenancy true --Settings true --Hangfire true

[[ $failed == 0 ]] && step "HEPSİ TAMAM" || step "HATALAR VAR (ayrıntı yukarıda)"
