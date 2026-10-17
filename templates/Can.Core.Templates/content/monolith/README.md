# CanAppTemplate

Can.Core paketleriyle kurulmuş monolith (Clean Architecture + CQRS) uygulama.

```
src/
  CanAppTemplate.Domain        entity'ler, value object'ler, domain event'leri
  CanAppTemplate.Application   Features/{Çoğul}/Commands|Queries — komut/sorgu + handler, doğrulama, yetki, eşleme
  CanAppTemplate.Persistence   AppDbContext, Configurations (entity eşlemeleri), seed
  CanAppTemplate.WebApi        uç noktalar, kimlik (JWT HttpOnly cookie'de), dış servisler
```

## İlk çalıştırma

1. Can.Core paket kaynağını `nuget.config`'e ekle (proje oluştururken verilmediyse).
2. Veritabanı: `docker compose up -d` (SQLite seçildiyse gerekmez).
3. Gizli değerler (appsettings'e YAZMA):

   ```bash
   cd src/CanAppTemplate.WebApi
   dotnet user-secrets set 'Security:Jwt:SigningKey' '<en az 32 karakter>'
   dotnet user-secrets set 'Security:VerificationCodeKey' '<en az 32 karakter, JWT anahtarından farklı>'
   dotnet user-secrets set 'Seed:AdminPassword' '<yönetici şifresi>'
   ```

   zsh'te `!` içeren değerleri tek tırnakla yaz.
4. `dotnet run --project src/CanAppTemplate.WebApi` → http://localhost:5280/docs

## Entity ekleme (Can.Core generator)

Entity'yi `CanAppTemplate.Domain` içinde yaz, ardından Rider'da entity dosyasına sağ tıkla → **Can.Core → Generate CRUD**
(ya da `can-gen crud Brand`). Üretilen dosyalar:

- `Application/Features/Brands/Commands/{Create,Update,Delete}/...`
- `Application/Features/Brands/Queries/{GetById,GetList}/...`
- `Application/Features/Brands/Constants/{BrandRules,BrandErrors,BrandPermissions}.cs`
- `Persistence/Configurations/BrandConfiguration.cs`
- `WebApi/Endpoints/BrandEndpoints.cs`

Generator kod içine `// can-gen:...` işaretlerinin olduğu yerlere ekleme yapar; bu satırları silme.
Senin doldurman gereken yerler `TODO(can-gen)` ile işaretlidir: IDE'nin TODO penceresinden (Rider: View → Tool
Windows → TODO) hepsini görebilirsin.

## Migration

```bash
dotnet ef migrations add Initial --project src/CanAppTemplate.Persistence --startup-project src/CanAppTemplate.WebApi
```

Migration yoksa uygulama ilk açılışta şemayı doğrudan oluşturur (yalnızca deneme için).
