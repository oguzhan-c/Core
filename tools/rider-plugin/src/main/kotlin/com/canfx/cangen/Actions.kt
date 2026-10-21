package com.canfx.cangen

import com.intellij.openapi.actionSystem.ActionUpdateThread
import com.intellij.openapi.actionSystem.AnAction
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.actionSystem.CommonDataKeys
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.VirtualFile
import java.io.File

/**
 * Entity dosyasına sağ tıklayınca görünür ve o entity için CRUD üretir. Yalnızca Domain projesindeki, doğrudan
 * Entity&lt;TId&gt; / AggregateRoot&lt;TId&gt; ya da Audited türevlerinden türeyen sınıflarda etkindir (can-gen ile aynı kural);
 * diğer .cs dosyalarında menüde görünmez.
 */
class GenerateCrudAction : AnAction() {
    override fun getActionUpdateThread(): ActionUpdateThread = ActionUpdateThread.BGT

    override fun update(e: AnActionEvent) {
        val entity = if (e.project != null) e.getData(CommonDataKeys.VIRTUAL_FILE)?.let(EntityDetector::entityName) else null
        e.presentation.isEnabledAndVisible = entity != null
        if (entity != null) e.presentation.text = "CanFx: Generate CRUD for $entity..."
    }

    override fun actionPerformed(e: AnActionEvent) {
        val project = e.project ?: return
        val file = e.getData(CommonDataKeys.VIRTUAL_FILE) ?: return
        val entity = EntityDetector.entityName(file) ?: return
        CanGenActions.generateFor(project, entity, file.path, directoryOf(file))
    }

    private fun directoryOf(file: VirtualFile): String = file.parent?.path ?: File(file.path).parent
}

/** Dosyanın can-gen'in kabul edeceği bir entity olup olmadığını hızlıca (metinden) anlar. */
internal object EntityDetector {
    private const val MAX_SIZE = 512 * 1024

    private val entityClass = Regex(
        """\bclass\s+(\w+)\s*(?:<[^>{]*>)?\s*(?:\([^)]*\))?\s*:\s*(?:[\w.]+\.)?""" +
            """(?:Entity|AggregateRoot|AuditedEntity|AuditedAggregateRoot|FullAuditedEntity|FullAuditedAggregateRoot)\s*<"""
    )

    /** Entity sınıfının adı; entity değilse ya da Domain projesinde değilse null. */
    fun entityName(file: VirtualFile): String? {
        if (file.isDirectory || file.extension != "cs" || file.length > MAX_SIZE) return null
        if (!isInDomainProject(file)) return null

        val text = FileDocumentManager.getInstance().getCachedDocument(file)?.text
            ?: runCatching { String(file.contentsToByteArray(), file.charset) }.getOrNull()
            ?: return null
        return entityClass.find(text)?.groupValues?.get(1)
    }

    /** Dosyanın yukarısında adı ".Domain" ile biten bir proje klasörü (ya da .csproj) var mı. */
    private fun isInDomainProject(file: VirtualFile): Boolean {
        var dir = file.parent
        while (dir != null) {
            if (dir.name.endsWith(".Domain", ignoreCase = true)) return true
            if (dir.children.any { it.extension == "csproj" }) return dir.children.any { it.name.endsWith(".Domain.csproj", ignoreCase = true) }
            dir = dir.parent
        }
        return false
    }
}

internal object CanGenActions {
    /** Seçenek penceresini gösterir; onaylanırsa kaydedilmemiş belgeleri yazar ve üretimi başlatır. */
    fun generateFor(project: Project, entity: String, entityPath: String, workingDirectory: String) {
        val options = GenerateCrudOptions()
        if (!GenerateCrudDialog(project, entity, options).showAndGet()) return

        // Entity dosyasındaki son değişiklikler diskte olsun (can-gen dosyadan okur).
        ApplicationManager.getApplication().runWriteAction { FileDocumentManager.getInstance().saveAllDocuments() }

        CanGen.generateCrud(project, entity, entityPath, workingDirectory, options)
    }
}
