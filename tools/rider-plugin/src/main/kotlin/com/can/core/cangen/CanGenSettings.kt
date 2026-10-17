package com.can.core.cangen

import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.components.PersistentStateComponent
import com.intellij.openapi.components.State
import com.intellij.openapi.components.Storage
import com.intellij.openapi.options.BoundConfigurable
import com.intellij.openapi.ui.DialogPanel
import com.intellij.ui.dsl.builder.AlignX
import com.intellij.ui.dsl.builder.bindSelected
import com.intellij.ui.dsl.builder.bindText
import com.intellij.ui.dsl.builder.panel

/** Eklenti ayarları (Settings → Tools → Can.Core Generator). */
@State(name = "CanGenSettings", storages = [Storage("can-gen.xml")])
class CanGenSettings : PersistentStateComponent<CanGenSettings.State> {
    class State {
        /**
         * can-gen komutu. Boşsa PATH'te ve ~/.dotnet/tools altında aranır. Geliştirirken kaynak koddan da çalıştırılabilir:
         * dotnet run --project /yol/Core/tools/Can.Core.Generator.Cli --
         */
        var command: String = ""

        /** Üretimden sonra Create komut dosyası açılsın. */
        var openGeneratedFiles: Boolean = true
    }

    private var myState = State()

    override fun getState(): State = myState

    override fun loadState(state: State) {
        myState = state
    }

    companion object {
        fun getInstance(): CanGenSettings = ApplicationManager.getApplication().getService(CanGenSettings::class.java)
    }
}

class CanGenConfigurable : BoundConfigurable("Can.Core Generator") {
    override fun createPanel(): DialogPanel {
        val state = CanGenSettings.getInstance().state
        return panel {
            row("can-gen komutu:") {
                textField()
                    .bindText(state::command)
                    .align(AlignX.FILL)
                    .comment(
                        "Boş bırakılırsa PATH'te ve ~/.dotnet/tools altında aranır. Kurulum: " +
                            "dotnet tool install -g Can.Core.Generator.Cli --add-source &lt;klasör&gt; --prerelease<br/>" +
                            "Kaynak koddan çalıştırmak için: dotnet run --project /yol/Core/tools/Can.Core.Generator.Cli --"
                    )
            }
            row {
                checkBox("Üretimden sonra dosyaları aç").bindSelected(state::openGeneratedFiles)
            }
        }
    }
}
