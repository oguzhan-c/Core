// can-gen Rider eklentisi: yalnızca arayüz (IntelliJ Platform tarafı). Kodu can-gen komut satırı aracı üretir; eklenti onu
// çağırır, sonucu bildirir ve üretilen dosyaları açar. Derleme: ./gradlew buildPlugin → build/distributions/*.zip
plugins {
    id("org.jetbrains.kotlin.jvm") version "2.2.20"
    id("org.jetbrains.intellij.platform") version "2.19.0"
}

group = "com.can.core"
version = providers.gradleProperty("pluginVersion").get()

kotlin {
    jvmToolchain(21)
}

repositories {
    mavenCentral()
    intellijPlatform {
        defaultRepositories()
    }
}

dependencies {
    intellijPlatform {
        // Rider çok işletim sistemli arşivden indirilir; JetBrains Runtime ayrıca eklenir.
        rider(providers.gradleProperty("platformVersion")) {
            useInstaller = false
        }
        jetbrainsRuntime()
    }
}

intellijPlatform {
    pluginConfiguration {
        version = providers.gradleProperty("pluginVersion")
        ideaVersion {
            sinceBuild = providers.gradleProperty("pluginSinceBuild")
            untilBuild = provider { null }
        }
    }
    buildSearchableOptions = false
}
