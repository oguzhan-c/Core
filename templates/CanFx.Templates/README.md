# CanFx.Templates

[CanFx](https://github.com/oguzhan-c/Core) paketleriyle katmanlı (DDD / Clean Architecture) .NET 10 çözümü oluşturur.

```bash
dotnet new install CanFx.Templates
dotnet new canfx-monolith -n MyShop                  # ya da: can-gen new MyShop (seçenekleri sorar)
dotnet new canfx-monolith --help                     # tüm seçenekler
```

İçinde: Domain / Application / Persistence / Infrastructure / WebAPI katmanları, JWT (HttpOnly cookie) ile kimlik,
e-posta doğrulama, 2FA (OTP, e-posta), passkey, rol / yetki yönetimi, isteğe bağlı multi-tenancy, Redis, event bus +
outbox, Hangfire, dosya depolama, arama, SignalR, SMS, ayarlar, raporlama ve OpenTelemetry.

Kimlik anahtar tipi seçilebilir: `--IdKeyType guid|int|long`. Veritabanı sağlayıcısı seçilmez: oluşturulan projedeki
`TODO(can-gen)` işaretli yerlerde ayarlanır (ayrıntı: projenin README'si).

Entity'den CRUD üretmek için: `dotnet tool install -g CanFx.Generator.Cli` → `can-gen generate crud <Entity>`.
