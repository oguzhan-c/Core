# can-gen

Can.Core şablonuyla oluşturulmuş çözümlerde entity'den CRUD (CQRS) kodu üretir.

## Kurulum (yerel)

```bash
build/pack-local.sh --install      # Core + can-gen + şablon, aynı yeni yerel sürümle paketlenir ve kurulur
```

## Kullanım

```bash
can-gen new MyShop                                   # şablonla çözüm (veritabanı ve özellikler sorulur)
can-gen generate crud                                # entity ve mekanizmalar sorulur
can-gen generate crud Brand -c -l -t -s -d           # soru sormadan
can-gen generate command SyncBrand Brands -e Put     # özelliğe komut
can-gen generate query GetBrandByName Brands         # özelliğe sorgu
can-gen generate dynamic-query Brand                 # CRUD'a sonradan dinamik sorgu
can-gen entities | features                          # listeler
can-gen --help | can-gen generate --help
```

Mekanizmalar (nArchitecture'daki gibi): `-c` caching, `-l` logging, `-t` transaction, `-s` secured, `-d` dynamic query.
Hiçbiri verilmezse sorulur; etkileşimsiz çalışmada (betik, `--json`) hepsi açıktır.

Diğer: `--project <klasör>`, `--plural <Çoğul>`, `-e/--endpoint-method`, `--force`, `--dry-run`, `--json` (IDE eklentileri).

## Sürüm

Core, can-gen ve şablon aynı sürümle paketlenir: `build/pack-local.sh --install` (yerel, `0.1.0-local.<zaman>`), CI'da
`build/ci.sh`. Sürüm öneki kökteki `Directory.Build.props` içindeki `VersionPrefix`.

## Rider eklentisi

`tools/rider-plugin`: Rider'da sağ tık → Can.Core: Generate CRUD. Derleme ve kurulum: [rider-plugin/README.md](rider-plugin/README.md).
