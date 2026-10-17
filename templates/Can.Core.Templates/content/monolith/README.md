# CanAppTemplate

Can.Core paketleriyle nArchitecture düzeninde kurulmuş monolith (Clean Architecture + CQRS) uygulama.

```
src/
  CanAppTemplate.Domain           Entities/, Constants/
  CanAppTemplate.Application      Features/{Çoğul}/Commands|Queries|Constants|Profiles|Rules
                                  Services/Repositories (I{Entity}Repository), Services/AuthService ...
                                  ApplicationServiceRegistration.cs
  CanAppTemplate.Persistence      Contexts/BaseDbContext, EntityConfigurations/, Repositories/, Seeding/
                                  PersistenceServiceRegistration.cs
  CanAppTemplate.Infrastructure   dış servisler (e-posta, dosya, arama, event bus, Redis, Hangfire ...)
                                  InfrastructureServiceRegistration.cs
  CanAppTemplate.WebAPI           Controllers/, kimlik akışı (JWT HttpOnly cookie'de), Program.cs
```

Hatalar exception olarak fırlar (`BusinessException` → 400, `NotFoundException` → 404, `ValidationException` → 400 ...)
ve ProblemDetails yanıtına çevrilir.

## İlk çalıştırma

1. Can.Core paket kaynağını `nuget.config`'e ekle (proje oluştururken verilmediyse).
2. Veritabanı: `docker compose up -d` (SQLite seçildiyse gerekmez).
3. Gizli değerler (appsettings'e YAZMA):

   ```bash
   cd src/CanAppTemplate.WebAPI
   dotnet user-secrets set 'Security:Jwt:SigningKey' '<en az 32 karakter>'
   dotnet user-secrets set 'Security:VerificationCodeKey' '<en az 32 karakter, JWT anahtarından farklı>'
   dotnet user-secrets set 'Seed:AdminPassword' '<yönetici şifresi>'
   ```

   zsh'te `!` içeren değerleri tek tırnakla yaz.
4. `dotnet run --project src/CanAppTemplate.WebAPI` → http://localhost:5280/docs

## Entity ekleme (can-gen)

Entity'yi `CanAppTemplate.Domain/Entities` içinde yaz, ardından Rider'da entity dosyasına sağ tıkla → **Can.Core → Generate
CRUD** (ya da terminalde `can-gen generate crud Brand`; komut/sorgu eklemek için `can-gen generate command|query`). Üretilen dosyalar:

- `Application/Features/Brands/Commands/{Create,Update,Delete}/` — komut + handler, validator, `Created/Updated/DeletedBrandResponse`
- `Application/Features/Brands/Queries/{GetById,GetList,GetListByDynamic}/` — sorgu + handler, `GetByIdBrandResponse`, `GetListBrandListItemDto`
- `Application/Features/Brands/{Constants,Profiles,Rules}/` — `BrandsMessages`, `BrandsOperationClaims`, `MappingProfiles`, `BrandBusinessRules`
- `Application/Services/Repositories/IBrandRepository.cs`, `Persistence/Repositories/BrandRepository.cs`
- `Persistence/EntityConfigurations/BrandConfiguration.cs`
- `WebAPI/Controllers/BrandsController.cs` (`/api/v1/Brands`)

Generator `// can-gen:...` işaretlerinin olduğu yerlere ekleme yapar; bu satırları silme. Senin doldurman gereken yerler
`TODO(can-gen)` ile işaretlidir (Rider: View → Tool Windows → TODO).

## Migration

```bash
dotnet ef migrations add Initial --project src/CanAppTemplate.Persistence --startup-project src/CanAppTemplate.WebAPI
```

Migration yoksa uygulama ilk açılışta şemayı doğrudan oluşturur (yalnızca deneme için).
