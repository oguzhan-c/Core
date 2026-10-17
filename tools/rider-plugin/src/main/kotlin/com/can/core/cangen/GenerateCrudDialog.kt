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

    // Mekanizmalar (nArchitecture'daki gibi)
    var caching: Boolean = true
    var logging: Boolean = true
    var transaction: Boolean = true
    var secured: Boolean = true
    var dynamicQuery: Boolean = true
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
        group("Mekanizmalar") {
            row { checkBox("Caching (sorgular önbelleğe alınır)").bindSelected(options::caching) }
            row { checkBox("Logging (komutlar loglanır)").bindSelected(options::logging) }
            row { checkBox("Transaction (kapalıysa handler kendisi kaydeder)").bindSelected(options::transaction) }
            row { checkBox("Secured Operation (yetki kontrolü)").bindSelected(options::secured) }
            row { checkBox("Dynamic Query (GetListByDynamic)").bindSelected(options::dynamicQuery) }
        }
        row {
            checkBox("Var olan dosyaların üzerine yaz (--force)").bindSelected(options::force)
        }
        row {
            checkBox("Yalnızca önizle, diske yazma (--dry-run)").bindSelected(options::dryRun)
        }
    }
}
