#!/usr/bin/env bash
# Şablonları yerelde doğrular: Core, can-gen ve şablonu yeni bir yerel sürümle paketleyip kurar, farklı seçeneklerle
# üç çözüm üretir, kurulu can-gen ile CRUD üretip her birini derler. Çıktı: <repo>/templates.log
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PACKAGES="$ROOT/artifacts/packages"
TEMPLATES="$ROOT/artifacts/templates"
OUT="$ROOT/artifacts/template-test"
LOG="$ROOT/templates.log"

: > "$LOG"
step() { echo "=== $*" | tee -a "$LOG"; }

# Core, can-gen ve şablon aynı yeni yerel sürümle paketlenir ve kurulur (build/pack-local.sh).
step "Paketleme ve kurulum (build/pack-local.sh --install)"
"$ROOT/build/pack-local.sh" --install >>"$LOG" 2>&1 || { step "HATA: paketleme/kurulum"; exit 1; }
VERSION="$(cat "$ROOT/artifacts/local-version.txt")"
step "Sürüm: $VERSION"

step "Can.Core.Application testleri"
dotnet run --project "$ROOT/tests/Can.Core.Application.Tests" >>"$LOG" 2>&1 && step "TAMAM: Can.Core.Application testleri" || { step "HATA: Can.Core.Application testleri"; exit 1; }

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

variant Minimal --IdKeyType int --Otp false --EmailTwoFactor false --Passkey false --AuditTrail false
variant Full --MultiTenancy true --ExternalLogin true --Redis true --EventBus true --Hangfire true \
  --FileStorage true --Search true --Realtime true --Sms true --Settings true --Reporting true --OpenTelemetry true
variant Tenant --IdKeyType long --ExternalLogin true --MultiTenancy true --Settings true --Hangfire true

# ---------------------------------------------------------------- can-gen
# Üretilen çözümlere iki entity eklenir (biri fabrika/Update metotlu, biri metotsuz: TODO yolları) ve CRUD üretilip
# yeniden derlenir.
step "can-gen testleri"
if dotnet run --project "$ROOT/tests/Can.Core.Generator.Tests" >>"$LOG" 2>&1; then step "TAMAM: can-gen testleri"; else step "HATA: can-gen testleri"; failed=1; fi

# Kurulu (paketlenmiş) can-gen kullanılır: paketin kendisi de denenmiş olur.
CANGEN=("$HOME/.dotnet/tools/can-gen")
step "can-gen sürümü: $("${CANGEN[@]}" version 2>&1)"

generate() {
  local name="$1"
  local domain="$OUT/$name/src/$name.Domain"
  [[ -d "$domain" ]] || return
  step "can-gen: $name (Brand, Tag)"
  mkdir -p "$domain/Catalog" "$domain/Tags"
  cat > "$domain/Catalog/Brand.cs" <<CS
using Can.Core.Domain.Auditing;
using Can.Core.Domain.Results;

namespace $name.Domain.Catalog;

public enum BrandTier { Standard, Premium }

[Audited]
public sealed class Brand : FullAuditedAggregateRoot<Guid>
{
    public const int NameMaxLength = 60;
    public const int DescriptionMaxLength = 500;

    private Brand() => Name = string.Empty;

    private Brand(Guid id) : base(id) => Name = string.Empty;

    public string Name { get; private set; }

    public string? Description { get; private set; }

    public bool IsActive { get; private set; } = true;

    public BrandTier Tier { get; private set; }

    public static Result<Brand> Create(string name, string? description, BrandTier tier) =>
        new Brand(Guid.CreateVersion7()) { Name = name.Trim(), Description = description, Tier = tier };

    public Result<Success> Update(string name, string? description, bool isActive)
    {
        Name = name.Trim();
        Description = description;
        IsActive = isActive;
        return Result.Success;
    }
}
CS
  cat > "$domain/Tags/Tag.cs" <<CS
using Can.Core.Domain.Entities;

namespace $name.Domain.Tags;

public sealed class Tag : Entity<int>
{
    public string Name { get; set; } = string.Empty;

    public int UsageCount { get; set; }
}
CS
  run_cangen() { "${CANGEN[@]}" "$@" --project "$OUT/$name" >>"$LOG" 2>&1 || { step "HATA: can-gen $* ($name)"; failed=1; }; }
  run_cangen generate crud Brand                              # tüm mekanizmalar
  run_cangen generate crud Tag -s                             # yalnızca yetki: transaction yok, önbellek yok, dinamik sorgu yok
  run_cangen generate dynamic-query Tag                       # dinamik sorgu sonradan
  run_cangen generate command SyncBrand Brands -e Put
  run_cangen generate query GetBrandByName Brands
  if dotnet build "$OUT/$name/$name.slnx" -c Debug >>"$LOG" 2>&1; then
    step "TAMAM: $name + can-gen derlendi"
  else
    step "HATA: $name + can-gen derlenemedi"; failed=1
  fi
}

generate Minimal
generate Full

# ---------------------------------------------------------------- standart
# Şablonla gelen (Auth, Account, Roles ...) ve can-gen'in ürettiği (Brands ...) özellikler aynı kurala uyar:
#   Features/{Özellik}/Constants/ + Rules/  (mesajlar, yetkiler, iş kuralları)
#   Commands|Queries/{İstek}/  {İstek}.cs (istek + handler), {İstek}Validator.cs, {...}Response.cs / {...}Dto.cs
#   hatalar exception (Rules) ile; istekler Result<T> dönmez.
standards() {
  local name="$1"
  local features="$OUT/$name/src/$name.Application/Features"
  [[ -d "$features" ]] || return
  local problems=""
  local f d

  f="$(grep -rlE 'IRequest<Result<|Task<Result<|IRequestHandler<[^>]*Result<' --include=*.cs "$features" || true)"
  [[ -n "$f" ]] && problems+=$'\n  Result dönen istek (exception + Rules kullanılmalı):\n'"$f"

  for f in $(grep -rlE 'AbstractValidator<' --include=*.cs "$features"); do
    grep -qE 'IRequest(<|,|[[:space:]]|$)' "$f" && problems+=$'\n  Validator istek dosyasında (ayrı {İstek}Validator.cs olmalı): '"$f"
  done

  for f in $(grep -rlE 'IRequest(<|,|[[:space:]]|$)' --include=*.cs "$features"); do
    grep -qE '(record|class) [A-Za-z0-9_]+(Response|Dto)\b' "$f" && problems+=$'\n  Yanıt/DTO istek dosyasında (ayrı dosya olmalı): '"$f"
  done

  for d in "$features"/*/; do
    [[ -d "$d/Constants" && -d "$d/Rules" ]] || problems+=$'\n  Constants/ ya da Rules/ eksik: '"$d"
  done

  if [[ -n "$problems" ]]; then
    step "HATA: $name standarda uymuyor:"
    echo "$problems" | tee -a "$LOG"
    failed=1
  else
    step "TAMAM: $name standarda uygun (Constants/Rules, ayrı validator ve yanıt dosyaları, Result yok)"
  fi
}
standards Minimal
standards Full
standards Tenant

[[ $failed == 0 ]] && step "HEPSİ TAMAM" || step "HATALAR VAR (ayrıntı yukarıda)"
