# CanAppTemplate

CanFx paketleriyle nArchitecture düzeninde kurulmuş monolith (Clean Architecture + CQRS) uygulama.

```
src/
  CanAppTemplate.Domain           Entities/, Constants/
  CanAppTemplate.Application      Features/{Çoğul}/Commands|Queries|Constants|Profiles|Rules
                                  Services/Repositories (I{Entity}Repository), Services/AuthService ...
                                  ApplicationServiceRegistration.cs
  CanAppTemplate.Persistence      Contexts/BaseDbContext, EntityConfigurations/, Repositories/, Seeding/
                                  PersistenceServiceRegistration.cs
  CanAppTemplate.Infrastructure   dış bağlantılar, her biri kendi klasöründe:
                                  Mailing/, Sms/, FileStorage/, Search/, Messaging/ (event bus), BackgroundJobs/ (Hangfire),
                                  Caching/ (Redis), Realtime/ (SignalR) → {Ad}ServiceRegistration.cs
                                  InfrastructureServiceRegistration.cs (yalnızca hepsini çağırır)
  CanAppTemplate.WebAPI           Controllers/, kimlik akışı (JWT HttpOnly cookie'de), Program.cs
```

Hatalar exception olarak fırlar (`BusinessException` → 400, `NotFoundException` → 404, `ValidationException` → 400 ...)
ve ProblemDetails yanıtına çevrilir.

## İlk çalıştırma

1. CanFx paketleri nuget.org'dan gelir; ek ayar gerekmez (yerel / özel kaynak için `nuget.config`'teki nota bak).
2. Veritabanı (şablon sağlayıcı seçmez; `TODO(can-gen)` işaretli yerleri doldur):
   - `Directory.Packages.props` + `Persistence` projesine sağlayıcı paketi (Npgsql / SqlServer / Sqlite),
   - `Persistence/PersistenceServiceRegistration.cs` → `options.UseNpgsql(...)` (ya da diğeri), `throw` satırını sil,
   - bağlantı dizesi: `appsettings.Development.json` ya da user-secrets (`ConnectionStrings:CanAppTemplate`),
   - gerekirse `docker-compose.yml`'deki veritabanı servisini aç, `docker compose up -d`.
   Doldurulmadan çalıştırılırsa uygulama açılışta "Veritabanı ayarlanmadı" hatasıyla durur.
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

Entity'yi `CanAppTemplate.Domain/Entities` içinde yaz, ardından Rider'da entity dosyasına sağ tıkla → **CanFx → Generate
CRUD** (ya da terminalde `can-gen generate crud Brand`; komut/sorgu eklemek için `can-gen generate command|query`). Üretilen dosyalar:

- `Application/Features/Brands/Commands/{Create,Update,Delete}/` — komut + handler, validator, `Created/Updated/DeletedBrandResponse`
- `Application/Features/Brands/Queries/{GetById,GetList,GetListByDynamic}/` — sorgu + handler, `GetByIdBrandResponse`, `GetListBrandListItemDto`
- `Application/Features/Brands/{Constants,Profiles,Rules}/` — `BrandsMessages`, `BrandsOperationClaims`, `MappingProfiles`, `BrandBusinessRules`
- `Application/Services/Repositories/IBrandRepository.cs`, `Persistence/Repositories/BrandRepository.cs`
- `Persistence/EntityConfigurations/BrandConfiguration.cs`
- `WebAPI/Controllers/BrandsController.cs` (`/api/v1/Brands`)

Generator `// can-gen:...` işaretlerinin olduğu yerlere ekleme yapar; bu satırları silme. Senin doldurman gereken yerler
`TODO(can-gen)` ile işaretlidir (Rider: View → Tool Windows → TODO).

## Kimlik anahtar tipi

Kullanıcı, rol, yetki, oturum, passkey gibi kimlik tablolarının Id tipi tek yerde seçilir: kökteki `Directory.Build.props`

```xml
<Using Include="System.Guid" Alias="IdentityKey" />   <!-- System.Int32 | System.Int64 -->
```

Kod her yerde `IdentityKey` kullanır (`Role<IdentityKey>`, `IRepository<AppUser, IdentityKey>` ...); yeni kayıt anahtarı
`IdentityKeys.New()` ile alınır (Guid: kodda v7, int / long: veritabanı üretir). Tipi değiştirince başka dosyaya dokunma,
yalnızca yeni bir migration ekle (var olan veritabanında veri taşıması gerekir). Uygulama entity'lerinin (ör. `Color : Entity<int>`)
anahtarı bundan bağımsızdır, her entity kendi tipini seçer.

## Roller ve yetkiler

- **Yetkiler kodla tanımlanır:** her özellik `Features/{Çoğul}/Constants/{Çoğul}OperationClaims.cs`, toplu liste
  `Application/Services/OperationClaims/AppOperationClaims.cs`. Açılışta veritabanı bu listeye eşitlenir; koddan çıkan
  yetki silinmez, "kullanımdan kalktı" olur ve kimseye etki etmez.
- **Dağıtım çalışırken yapılır** (`/api/identity/...`, yetkiler `IdentityPermissions`):
  - `GET/POST/PUT/DELETE /api/identity/roles` — rol listesi, oluştur, düzenle, sil
  - `POST /api/identity/roles/{id}/permissions/grant|revoke` — role yetki ver / al (`{ "operationClaimIds": [...] }`)
  - `GET /api/identity/operation-claims` — yetki kataloğu
  - `GET /api/identity/users/{userId}/authorization` — kullanıcının rolleri ve yetkileri
  - `POST|DELETE /api/identity/users/{userId}/roles/{roleId}` — rol ata / kaldır
  - `POST /api/identity/users/{userId}/permissions/grant|revoke` — doğrudan yetki ver / al
- **Yetki yükseltme yok** (Core: `IAccessGrantGuard`): kimse sahip olmadığı yetkiyi veremez, kapsamadığı rolü atayamaz,
  kendi rollerini/yetkilerini değiştiremez; Admin ve User sistem rolüdür (silinmez, adı değişmez); son yönetici kaldırılamaz.
  "Yetki verme yetkisi" de bir yetkidir: Admin, örneğin `identity.users.assign-role`'ü bir "Moderatör" rolüne verebilir;
  Moderatör yalnızca kendi kapsadığı rolleri dağıtabilir.
- **Değişiklik hemen geçerli:** roller ve yetkiler token'a yazılmaz; her istekte (1 dk önbellekli) veritabanından okunur
  (`UseCanPermissionResolution`). Birden fazla sunucuda değişiklik en geç önbellek süresi sonunda yansır.

## Migration

```bash
dotnet ef migrations add Initial --project src/CanAppTemplate.Persistence --startup-project src/CanAppTemplate.WebAPI
```

Migration yoksa uygulama ilk açılışta şemayı doğrudan oluşturur (yalnızca deneme için).
