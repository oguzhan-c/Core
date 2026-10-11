#!/usr/bin/env bash
# Tüm CI sistemlerinin (GitHub, GitLab, Azure DevOps, AWS CodeBuild, Google Cloud Build) çağırdığı ortak betik.
# Yerelde de aynı şekilde çalışır:
#   ./build/ci.sh            restore + build + test
#   ./build/ci.sh restore | build | test | client
#   ./build/ci.sh services | services-down   entegrasyon servislerini docker compose ile başlat / kaldır
# Ortam değişkenleri:
#   CONFIGURATION   Release (varsayılan) / Debug
#   ARTIFACTS       çıktı klasörü (varsayılan: artifacts)
#   CAN_RABBITMQ_URL, CAN_KAFKA_BOOTSTRAP, CAN_REDIS_URL, CAN_ELASTICSEARCH_URL, CAN_AZURE_SERVICEBUS_CONNECTION
#                   verilen entegrasyon testleri çalışır (servis hazır olana kadar beklenir), verilmeyenler atlanır.
set -euo pipefail

cd "$(dirname "$0")/.."
CONFIGURATION="${CONFIGURATION:-Release}"
ARTIFACTS="${ARTIFACTS:-artifacts}"
export CI=true                      # golden dosyası eksikse test yazmak yerine hata versin
export DOTNET_NOLOGO=true
export DOTNET_CLI_TELEMETRY_OPTOUT=true

log() { printf '\n==> %s\n' "$*"; }

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
  for pair in "RabbitMQ:CAN_RABBITMQ_URL" "Kafka:CAN_KAFKA_BOOTSTRAP" "Redis:CAN_REDIS_URL" "Elasticsearch:CAN_ELASTICSEARCH_URL"; do
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
  log "build ($CONFIGURATION)"
  dotnet build Core.slnx -c "$CONFIGURATION" --no-restore
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
VARS
}

services_down() {
  log "entegrasyon servisleri kaldırılıyor"
  docker compose -f ci/docker-compose.ci.yml down -v
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
  services) services_up ;;
  services-down) services_down ;;
  all) restore; build; run_tests ;;
  *) echo "Kullanım: $0 [all|restore|build|test|client|services|services-down]" >&2; exit 2 ;;
esac
