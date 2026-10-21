#!/usr/bin/env bash
# Tüm CI sistemlerinin (GitHub, GitLab, Azure DevOps, AWS CodeBuild, Google Cloud Build) çağırdığı ortak betik.
# Yerelde de aynı şekilde çalışır:
#   ./build/ci.sh            restore + build + test
#   ./build/ci.sh restore | build | test | client
#   ./build/ci.sh services | services-down   entegrasyon servislerini docker compose ile başlat / kaldır
#   ./build/ci.sh pack       NuGet paketleri + proje şablonu (artifacts/packages)
#   ./build/ci.sh publish    etiketli derlemede (v1.2.0) paketleri yayınlar; etiket yoksa hiçbir şey yapmaz
#   ./build/ci.sh version    hesaplanan sürümü yazar
# Ortam değişkenleri:
#   CONFIGURATION   Release (varsayılan) / Debug
#   ARTIFACTS       çıktı klasörü (varsayılan: artifacts)
#   CAN_RABBITMQ_URL, CAN_KAFKA_BOOTSTRAP, CAN_REDIS_URL, CAN_ELASTICSEARCH_URL, CAN_AZURITE_URL,
#   CAN_AZURE_SERVICEBUS_CONNECTION
#                   verilen entegrasyon testleri çalışır (servis hazır olana kadar beklenir), verilmeyenler atlanır.
#   NUGET_API_KEY   yayın anahtarı (yalnızca publish); NUGET_SOURCE yayın adresi (varsayılan nuget.org)
set -euo pipefail

cd "$(dirname "$0")/.."
CONFIGURATION="${CONFIGURATION:-Release}"
ARTIFACTS="${ARTIFACTS:-artifacts}"
export CI=true                      # golden dosyası eksikse test yazmak yerine hata versin
export DOTNET_NOLOGO=true
export DOTNET_CLI_TELEMETRY_OPTOUT=true

log() { printf '\n==> %s\n' "$*"; }

# Azure DevOps tanımsız değişkeni "$(AD)" metni olarak geçirir: bunları tanımsız say
for variable in CAN_AZURE_SERVICEBUS_CONNECTION CAN_AZURITE_URL CAN_RABBITMQ_URL CAN_KAFKA_BOOTSTRAP CAN_REDIS_URL CAN_ELASTICSEARCH_URL NUGET_API_KEY NUGET_SOURCE; do
  if [[ "${!variable:-}" == '$('*')' ]]; then unset "$variable"; fi
done

# ------------------------------------------------------------------ sürüm
# Etiket: CI sisteminin değişkeninden, yoksa git'ten (HEAD tam olarak etiketliyse).
release_tag() {
  local tag="${CI_COMMIT_TAG:-${BITBUCKET_TAG:-${TAG_NAME:-}}}"               # GitLab, Bitbucket, Jenkins/Cloud Build
  if [[ -z "$tag" && "${GITHUB_REF_TYPE:-}" == tag ]]; then tag="$GITHUB_REF_NAME"; fi             # GitHub
  if [[ -z "$tag" && "${BUILD_SOURCEBRANCH:-}" == refs/tags/* ]]; then tag="${BUILD_SOURCEBRANCH#refs/tags/}"; fi   # Azure DevOps
  if [[ -z "$tag" && "${CODEBUILD_WEBHOOK_TRIGGER:-}" == tag/* ]]; then tag="${CODEBUILD_WEBHOOK_TRIGGER#tag/}"; fi # AWS
  if [[ -z "$tag" ]]; then tag="$(git describe --tags --exact-match 2>/dev/null || true)"; fi
  printf '%s' "$tag"
}

# v1.2.0 / v1.2.0-rc.1 etiketi → o sürüm (yayınlanır); yoksa VersionPrefix-ci.<derleme no> (yayınlanmaz).
compute_version() {
  local tag prefix number
  tag="$(release_tag)"
  if [[ "$tag" =~ ^v([0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?)$ ]]; then
    VERSION="${BASH_REMATCH[1]}"
    IS_RELEASE=true
    return
  fi
  prefix="$(sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' Directory.Build.props | head -n 1)"
  number="${GITHUB_RUN_NUMBER:-${CI_PIPELINE_IID:-${BUILD_BUILDID:-${BITBUCKET_BUILD_NUMBER:-${BUILD_NUMBER:-${CODEBUILD_BUILD_NUMBER:-}}}}}}"
  if [[ -n "$number" ]]; then VERSION="$prefix-ci.$number"; else VERSION="$prefix-local"; fi
  IS_RELEASE=false
}
compute_version

# host:port açılana kadar bekler (en çok 180 sn). Bash'in /dev/tcp'si: ek araç gerekmez.
wait_for() {
  local name="$1" host="$2" port="$3" deadline=$((SECONDS + 180))
  printf '    %s (%s:%s) bekleniyor' "$name" "$host" "$port"
  until (exec 3<>"/dev/tcp/$host/$port") 2>/dev/null; do
    if ((SECONDS > deadline)); then
      printf '\n    %s hazır değil; CI servis tanımını kontrol et.\n' "$name" >&2
      return 1
    fi
    printf '.'
    sleep 2
  done
  printf ' hazır\n'
}

# "amqp://u:p@host:5672/", "host:9092", "http://host:9200" → host ve port
host_port() {
  local value="${1#*://}"
  value="${value#*@}"
  value="${value%%/*}"
  printf '%s %s' "${value%%:*}" "${value##*:}"
}

wait_for_services() {
  for pair in "RabbitMQ:CAN_RABBITMQ_URL" "Kafka:CAN_KAFKA_BOOTSTRAP" "Redis:CAN_REDIS_URL" "Elasticsearch:CAN_ELASTICSEARCH_URL" "Azurite:CAN_AZURITE_URL"; do
    local name="${pair%%:*}" variable="${pair#*:}"
    local value="${!variable:-}"
    if [[ -n "$value" ]]; then
      read -r host port <<<"$(host_port "$value")"
      wait_for "$name" "$host" "$port"
    else
      printf '    %s: %s verilmedi, entegrasyon testleri atlanacak\n' "$name" "$variable"
    fi
  done
  if [[ -n "${CAN_ELASTICSEARCH_URL:-}" ]] && command -v curl >/dev/null; then
    # port açılsa da küme birkaç saniye "red" kalabilir
    curl -fsS --retry 30 --retry-delay 2 --retry-all-errors "${CAN_ELASTICSEARCH_URL%/}/_cluster/health?wait_for_status=yellow&timeout=60s" >/dev/null
  fi
}

restore() {
  log "restore"
  echo "    .NET SDK $(dotnet --version)"
  dotnet restore Core.slnx
}

build() {
  log "build ($CONFIGURATION, sürüm $VERSION)"
  dotnet build Core.slnx -c "$CONFIGURATION" --no-restore -p:Version="$VERSION"
}

run_tests() {
  log "entegrasyon servisleri"
  wait_for_services
  log "test ($CONFIGURATION)"
  rm -rf "$ARTIFACTS/test-results"
  # JUnit raporu: GitLab, Azure DevOps ve AWS CodeBuild sonuçları test sekmesinde gösterir
  dotnet test --solution Core.slnx -c "$CONFIGURATION" --no-build \
    --results-directory "$ARTIFACTS/test-results" --report-xunit-junit
}

# Entegrasyon servisleri (ci/docker-compose.ci.yml): Jenkins ve AWS CodeBuild kullanır, yerelde de çalışır.
services_up() {
  log "entegrasyon servisleri başlatılıyor (docker compose)"
  docker compose -f ci/docker-compose.ci.yml up -d
  cat <<'VARS'
    Testler için:
      export CAN_RABBITMQ_URL=amqp://guest:guest@localhost:5672/
      export CAN_KAFKA_BOOTSTRAP=localhost:9092
      export CAN_REDIS_URL=localhost:6379
      export CAN_ELASTICSEARCH_URL=http://localhost:9200
      export CAN_AZURITE_URL=http://localhost:10000/devstoreaccount1
VARS
}

services_down() {
  log "entegrasyon servisleri kaldırılıyor"
  docker compose -f ci/docker-compose.ci.yml down -v
}

pack() {
  log "pack (sürüm $VERSION)"
  rm -rf "$ARTIFACTS/packages"
  # --no-build: test edilen ikililer paketlenir (build aynı sürümle yapılmış olmalı)
  dotnet pack Core.slnx -c "$CONFIGURATION" --no-build -p:Version="$VERSION" -o "$ARTIFACTS/packages"
  # Proje şablonu (dotnet new canfx-monolith): aynı sürümle, aynı klasöre (publish hepsini birlikte gönderir)
  ./build/pack-templates.sh "$VERSION" "$ARTIFACTS/packages"
  printf '    %s paket\n' "$(find "$ARTIFACTS/packages" -name '*.nupkg' | wc -l | tr -d ' ')"
}

publish() {
  if [[ "$IS_RELEASE" != true ]]; then
    log "publish atlandı (etiketli derleme değil; sürüm $VERSION)"
    return
  fi
  log "publish $VERSION"
  if ! grep -qE "^## \[$VERSION\]" CHANGELOG.md; then
    echo "    CHANGELOG.md'de '## [$VERSION]' bölümü yok: önce değişiklikleri yaz." >&2
    exit 1
  fi
  if [[ -z "${NUGET_API_KEY:-}" ]]; then
    echo "    NUGET_API_KEY tanımlı değil (CI'ın gizli değişkenlerine ekle)." >&2
    exit 1
  fi
  # .snupkg sembol paketleri aynı klasörde olduğu için birlikte gider; aynı sürüm zaten varsa atlanır
  dotnet nuget push "$ARTIFACTS/packages/*.nupkg" \
    --api-key "$NUGET_API_KEY" \
    --source "${NUGET_SOURCE:-https://api.nuget.org/v3/index.json}" \
    --skip-duplicate
}

client() {
  log "reporting-react tip denetimi"
  (cd clients/reporting-react && npm install --no-audit --no-fund && npm run typecheck)
}

case "${1:-all}" in
  restore) restore ;;
  build) build ;;
  test) run_tests ;;
  client) client ;;
  pack) pack ;;
  publish) publish ;;
  version) echo "$VERSION" ;;
  services) services_up ;;
  services-down) services_down ;;
  all) restore; build; run_tests; pack ;;
  *) echo "Kullanım: $0 [all|restore|build|test|pack|publish|version|client|services|services-down]" >&2; exit 2 ;;
esac
