# can-gen Rider eklentisi

Rider'da entity dosyasına sağ tık (Solution Explorer ya da editör) → **Can.Core: Generate CRUD...**

Eklenti yalnızca arayüzdür: kodu `can-gen` komut satırı aracı üretir (`--json` ile çağrılır). Önce aracı kur:

```bash
dotnet pack tools/Can.Core.Generator.Cli -c Release -o artifacts/tools -p:Version=0.1.0-local
dotnet tool install -g Can.Core.Generator.Cli --add-source artifacts/tools --prerelease
```

Araç PATH'te ya da `~/.dotnet/tools` altında değilse Settings → Tools → Can.Core Generator'da komutu yaz
(kaynak koddan: `dotnet run --project /yol/Core/tools/Can.Core.Generator.Cli --`).

## Derleme

Gerekenler: JDK 21 ve (yalnızca ilk sefer, wrapper oluşturmak için) Gradle 9.

```bash
brew install openjdk@21 gradle
export JAVA_HOME="$(brew --prefix openjdk@21)/libexec/openjdk.jdk/Contents/Home"

cd tools/rider-plugin
gradle wrapper --gradle-version 9.1.0     # bir kez; gradlew dosyaları commit'lenir
./gradlew buildPlugin                     # → build/distributions/can-gen-rider-<pluginVersion>.zip
```

`gradle.properties` içindeki `platformVersion` yüklü Rider'ın ana sürümü olmalı (Rider → About).
İlk derleme Rider SDK'sını indirir (birkaç GB, bir kez).

Kurulum: Rider → Settings → Plugins → ⚙ → **Install Plugin from Disk...** → zip dosyası → Rider'ı yeniden başlat.
