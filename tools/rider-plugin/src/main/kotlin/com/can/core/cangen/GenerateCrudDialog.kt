package com.can.core.cangen

import com.intellij.openapi.project.Project
import com.intellij.openapi.ui.DialogWrapper
import com.intellij.ui.dsl.builder.AlignX
import com.intellij.ui.dsl.builder.bindSelected
import com.intellij.ui.dsl.builder.bindText
import com.intellij.ui.dsl.builder.panel
import javax.swing.JComponent

/** CRUD üretim seçenekleri. */
class GenerateCrudOptions {
    var plural: String = ""
    var force: Boolean = false
    var dryRun: Boolean = false
}

class GenerateCrudDialog(project: Project, private val entity: String, val options: GenerateCrudOptions) : DialogWrapper(project) {
    init {
        title = "Generate CRUD: $entity"
        setOKButtonText("Üret")
        init()
    }

    override fun createCenterPanel(): JComponent = panel {
        row {
            label(
                "<html>$entity için Create/Update/Delete komutları, GetById/GetList sorguları, DTO'lar, kurallar, yetkiler,<br/>" +
                    "EF yapılandırması ve uç noktalar üretilir. Tamamlanacak yerler <b>TODO(can-gen)</b> ile işaretlenir.</html>"
            )
        }
        row("Çoğul ad:") {
            textField()
                .bindText(options::plural)
                .align(AlignX.FILL)
                .comment("Boşsa İngilizce kurallarla türetilir (Category → Categories). Klasör, tablo ve adres adı olur.")
        }
        row {
            checkBox("Var olan dosyaların üzerine yaz (--force)").bindSelected(options::force)
        }
        row {
            checkBox("Yalnızca önizle, diske yazma (--dry-run)").bindSelected(options::dryRun)
        }
    }
}
