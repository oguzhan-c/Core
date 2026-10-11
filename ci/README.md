# CI

Asıl iş tek bir betikte: [`build/ci.sh`](../build/ci.sh) (restore → build → test, ayrıca arayüz paketinin tip denetimi).
Her CI sistemi için yalnızca ortamı (SDK, entegrasyon servisleri) hazırlayan kısa bir dosya var. Kullandığını seç,
diğerlerini silebilirsin; betik değişmediği için sistem değiştirmek yalnızca bu dosyayı değiştirmek demek.

| Sistem | Dosya | Nasıl açılır | Entegrasyon servisleri |
|---|---|---|---|
| GitHub Actions | `.github/workflows/ci.yml` | Depoya push etmek yeterli | Servis container'ları (localhost) |
| GitLab CI/CD | `.gitlab-ci.yml` (depo kökü) | Depoya push etmek yeterli | Servisler (takma adla: `rabbitmq`, `kafka` ...) |
| Azure DevOps | `ci/azure/azure-pipelines.yml` | Pipelines → New pipeline → "Existing Azure Pipelines YAML file" | Servis container'ları (localhost) |
| Bitbucket Pipelines | `bitbucket-pipelines.yml` (depo kökü) | Repository settings → Pipelines → Enable | Servisler (localhost), adım `size: 2x` |
| Jenkins | `ci/jenkins/Jenkinsfile` | Pipeline → "Pipeline script from SCM" → Script Path | `docker compose` (`ci/docker-compose.ci.yml`); ajanda Docker gerekli |
| AWS CodeBuild | `ci/aws/buildspec.yml` | Build projesinde buildspec yolu olarak ver; **Privileged** açık | `docker compose` (`ci/docker-compose.ci.yml`) |
| Google Cloud Build | `ci/gcp/cloudbuild.yaml` | Trigger'da yapılandırma dosyası olarak ver | `ci/gcp/start-services.sh` (`cloudbuild` ağında, adla) |

Hepsinde RabbitMQ, Kafka, Redis ve Elasticsearch testleri çalışır. Azure Service Bus'ın container'ı olmadığı için
bağlantı dizesi sistemin gizli değişkenlerine `CAN_AZURE_SERVICEBUS_CONNECTION` adıyla eklenirse çalışır, yoksa
atlanır (her dosyada yeri yorum olarak gösterildi). Bağlantı dizesini repoya yazma.

Kafka ve Elasticsearch birlikte yaklaşık 2-3 GB bellek ister: makine/ajan en az 7-8 GB olmalı.

## Sürüm ve yayın

- Sürüm tek yerde: `Directory.Build.props` → `VersionPrefix` (sıradaki sürüm). Etiketsiz derlemeler
  `VersionPrefix-ci.<derleme no>` sürümüyle paketlenir (artifact olarak indirilebilir, yayınlanmaz).
- `v1.2.0` (ya da `v1.2.0-rc.1`) etiketi push edilince sürüm etiketten alınır, testler geçerse paketler yayınlanır.
- Yayın için CI'ın gizli değişkenlerine `NUGET_API_KEY` ekle. nuget.org dışı bir kaynak (GitHub Packages, Azure
  Artifacts, GitLab Package Registry ...) için `NUGET_SOURCE`'u da ver.
- `CHANGELOG.md`'de `## [1.2.0]` bölümü yoksa yayın durur.
- Paketlerde Source Link (koda adım adım girilir), `.snupkg` sembol paketi ve README vardır.

```bash
# yayın adımları
#  1. CHANGELOG.md: "## [Unreleased]" → "## [0.1.0] - 2026-10-11", üstüne yeni boş "## [Unreleased]"
#  2. Directory.Build.props: VersionPrefix → 0.2.0 (sıradaki)
git commit -am "chore(release): 0.1.0"
git tag v0.1.0
git push && git push --tags
```

Etiketle tetikleme her sistemde açık; ek ayar gerekenler: Jenkins (Multibranch → "Discover tags"), AWS CodeBuild
(webhook filtresi `HEAD_REF` = `^refs/tags/v.*`), Google Cloud Build (etiket için ayrı trigger, `^v.*`).

## Yerelde

```bash
./build/ci.sh                     # restore + build + test + pack (Release); CAN_* verilmezse entegrasyon testleri atlanır
./build/ci.sh version             # hesaplanan sürüm

./build/ci.sh services            # servisleri docker compose ile başlat (Northwind'in container'ları açıksa önce durdur: aynı portlar)
export CAN_RABBITMQ_URL=amqp://guest:guest@localhost:5672/ CAN_KAFKA_BOOTSTRAP=localhost:9092 \
       CAN_REDIS_URL=localhost:6379 CAN_ELASTICSEARCH_URL=http://localhost:9200
./build/ci.sh
./build/ci.sh services-down
```

- Betik, verilen servislerin portu açılana kadar bekler (en çok 3 dk); Elasticsearch için kümenin hazır olmasını da bekler.
- Test sonuçları `artifacts/test-results` altında JUnit biçiminde: GitLab, Azure DevOps, Bitbucket, Jenkins ve
  CodeBuild test sekmesinde gösterir, GitHub ve Cloud Build'de dosya olarak kalır.
- CI'da (`CI=true`) eksik golden dosyası hata verir; yerelde testi çalıştırıp oluşan dosyayı commit'le.
- Uyarılar hata sayılır (`TreatWarningsAsErrors`); yerelde derlenen her şey CI'da da derlenir.
