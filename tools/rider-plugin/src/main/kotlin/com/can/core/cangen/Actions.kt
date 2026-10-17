package com.can.core.cangen

import com.intellij.openapi.actionSystem.ActionUpdateThread
import com.intellij.openapi.actionSystem.AnAction
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.actionSystem.CommonDataKeys
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.progress.ProgressManager
import com.intellij.openapi.project.Project
import com.intellij.openapi.ui.Messages
import com.intellij.openapi.ui.popup.JBPopupFactory
import com.intellij.openapi.vfs.VirtualFile
import java.io.File

/**
 * Seçili / açık entity dosyası (.cs) için CRUD üretir. Dosyanın bir entity olup olmadığını can-gen denetler
 * (Entity&lt;TId&gt; / AggregateRoot&lt;TId&gt; ya da Audited türevlerinden türemeli).
 */
class GenerateCrudAction : AnAction() {
    override fun getActionUpdateThread(): ActionUpdateThread = ActionUpdateThread.BGT

    override fun update(e: AnActionEvent) {
        val file = e.getData(CommonDataKeys.VIRTUAL_FILE)
        e.presentation.isEnabledAndVisible = e.project != null && file != null && !file.isDirectory && file.extension == "cs"
    }

    override fun actionPerformed(e: AnActionEvent) {
        val project = e.project ?: return
        val file = e.getData(CommonDataKeys.VIRTUAL_FILE) ?: return
        CanGenActions.generateFor(project, file.nameWithoutExtension, file.path, directoryOf(file))
    }

    private fun directoryOf(file: VirtualFile): String = file.parent?.path ?: File(file.path).parent
}

/** Domain'deki entity'leri listeler (can-gen entities), seçileni üretir. */
class ChooseEntityAction : AnAction() {
    override fun getActionUpdateThread(): ActionUpdateThread = ActionUpdateThread.BGT

    override fun update(e: AnActionEvent) {
        e.presentation.isEnabledAndVisible = e.project?.basePath != null
    }

    override fun actionPerformed(e: AnActionEvent) {
        val project = e.project ?: return
        val start = e.getData(CommonDataKeys.VIRTUAL_FILE)?.let { if (it.isDirectory) it.path else it.parent?.path } ?: project.basePath ?: return

        val result = ProgressManager.getInstance().runProcessWithProgressSynchronously<CanGenResult, RuntimeException>(
            { CanGenRunner.run(start, "entities") },
            "can-gen: entity'ler okunuyor",
            true,
            project,
        )

        if (!result.ok) {
            Messages.showErrorDialog(project, result.error ?: "Entity'ler okunamadı.", "can-gen")
            return
        }

        val entities = result.json["entities"].arr().map { it.obj() }.mapNotNull { e2 ->
            val name = e2.str("name") ?: return@mapNotNull null
            name to (e2.str("filePath") ?: "")
        }

        if (entities.isEmpty()) {
            Messages.showInfoMessage(project, "Domain projesinde entity bulunamadı.", "can-gen")
            return
        }

        JBPopupFactory.getInstance()
            .createPopupChooserBuilder(entities.map { it.first })
            .setTitle("CRUD üretilecek entity")
            .setNamerForFiltering { it }
            .setItemChosenCallback { name ->
                val path = entities.first { it.first == name }.second
                CanGenActions.generateFor(project, name, path, File(path).parent ?: start)
            }
            .createPopup()
            .showCenteredInCurrentWindow(project)
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
