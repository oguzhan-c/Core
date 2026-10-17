# can-gen

Can.Core şablonuyla oluşturulmuş çözümlerde entity'den CRUD (CQRS) kodu üretir.

## Kurulum (yerel)

```bash
dotnet pack tools/Can.Core.Generator.Cli -c Release -o artifacts/tools
dotnet tool install -g Can.Core.Generator.Cli --add-source artifacts/tools --prerelease
# güncelleme: dotnet tool update -g Can.Core.Generator.Cli --add-source artifacts/tools --prerelease
```

## Kullanım

```bash
can-gen entities                         # Domain'deki entity'ler
can-gen crud Brand                       # CRUD üret
can-gen crud src/MyApp.Domain/Catalog/Brand.cs --dry-run
can-gen crud Category --plural Categories --force
```

Seçenekler: `--project <klasör>`, `--plural <Çoğul>`, `--force` (var olan dosyaların üzerine yaz), `--dry-run`,
`--json` (IDE eklentileri için).

## Rider eklentisi

`tools/rider-plugin`: Rider'da sağ tık → Can.Core → Generate CRUD. Derleme ve kurulum: [rider-plugin/README.md](rider-plugin/README.md).
