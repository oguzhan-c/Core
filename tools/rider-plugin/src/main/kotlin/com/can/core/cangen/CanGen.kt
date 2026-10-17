package com.can.core.cangen

import com.intellij.notification.NotificationAction
import com.intellij.notification.NotificationGroupManager
import com.intellij.notification.NotificationType
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.fileEditor.FileEditorManager
import com.intellij.openapi.progress.ProgressIndicator
import com.intellij.openapi.progress.Task
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.LocalFileSystem
import com.intellij.openapi.vfs.VfsUtil
import java.io.File

/** CRUD üretimini arka planda çalıştırır, dosyaları yeniler, sonucu bildirir ve üretilen dosyaları açar. */
object CanGen {
    /**
     * @param entity Bildirimlerde gösterilen entity adı.
     * @param target can-gen'e verilen entity adı ya da dosya yolu.
     */
    fun generateCrud(project: Project, entity: String, target: String, workingDirectory: String, options: GenerateCrudOptions) {
        val arguments = buildList {
            add("crud")
            add(target)
            if (options.plural.isNotBlank()) {
                add("--plural")
                add(options.plural.trim())
            }
            if (options.caching) add("--caching")
            if (options.logging) add("--logging")
            if (options.transaction) add("--transaction")
            if (options.secured) add("--secured")
            if (options.dynamicQuery) add("--dynamic-query")
            // Hiçbiri seçilmezse can-gen "hepsi açık" sayar: en az birini gönder.
            if (!options.caching && !options.logging && !options.transaction && !options.secured && !options.dynamicQuery) add("--no-mechanisms")
            if (options.force) add("--force")
            if (options.dryRun) add("--dry-run")
        }

        object : Task.Backgroundable(project, "can-gen: $entity için CRUD üretiliyor", true) {
            override fun run(indicator: ProgressIndicator) {
                indicator.isIndeterminate = true
                val result = CanGenRunner.run(workingDirectory, *arguments.toTypedArray())
                val changes = CanGenRunner.changes(result)

                if (!options.dryRun) refresh(changes)

                ApplicationManager.getApplication().invokeLater {
                    if (project.isDisposed) return@invokeLater
                    report(project, entity, result, changes, options.dryRun)
                }
            }
        }.queue()
    }

    private fun refresh(changes: List<CanGenChange>) {
        val files = changes.filter { it.kind in setOf("created", "overwritten", "updated") }.map { File(it.path) }
        if (files.isNotEmpty()) LocalFileSystem.getInstance().refreshIoFiles(files, false, false, null)
        files.mapNotNull { it.parentFile }.distinct().forEach { VfsUtil.markDirtyAndRefresh(false, true, true, it) }
    }

    private fun report(project: Project, entity: String, result: CanGenResult, changes: List<CanGenChange>, dryRun: Boolean) {
        val group = NotificationGroupManager.getInstance().getNotificationGroup("can-gen")

        if (!result.ok && changes.isEmpty()) {
            group.createNotification("can-gen: $entity", (result.error ?: "Bilinmeyen hata").toHtml(), NotificationType.ERROR).notify(project)
            return
        }

        val solution = result.json.str("solution") ?: ""
        fun relative(path: String) = if (solution.isNotEmpty() && path.startsWith(solution)) path.substring(solution.length).trimStart('/', '\\') else path

        val created = changes.filter { it.kind == "created" || it.kind == "overwritten" }
        val updated = changes.filter { it.kind == "updated" }
        val skipped = changes.filter { it.kind == "skipped" }
        val failed = changes.filter { it.kind == "failed" }

        val entityJson = result.json["entity"].obj()
        val lines = buildList {
            if (dryRun) add("<i>Önizleme: diske yazılmadı.</i>")
            add("${created.size} dosya üretildi, ${updated.size} dosyaya ekleme yapıldı" + if (skipped.isNotEmpty()) ", ${skipped.size} dosya zaten vardı (--force ile üzerine yazılır)." else ".")
            failed.forEach { add("⚠ ${relative(it.path)}: ${it.note ?: "başarısız"}") }
            if (!entityJson.bool("hasFactory")) add("⚠ $entity'de Create(...) fabrika metodu ya da public constructor yok: oluşturma handler'ında TODO var.")
            if (!entityJson.bool("hasUpdate")) add("⚠ $entity'de Update(...) metodu ya da public setter'lar yok: güncelleme handler'ında TODO var.")
            add("Tamamlanacak yerler: <b>TODO(can-gen)</b> (View → Tool Windows → TODO).")
        }

        val type = when {
            failed.isNotEmpty() -> NotificationType.WARNING
            else -> NotificationType.INFORMATION
        }

        val notification = group.createNotification("can-gen: $entity", lines.joinToString("<br/>"), type)
        val files = (created + updated).map { it.path }
        if (files.isNotEmpty() && !dryRun) {
            notification.addAction(NotificationAction.createSimple("Dosyaları aç") { open(project, files) })
        }
        notification.notify(project)

        if (!dryRun && CanGenSettings.getInstance().state.openGeneratedFiles) {
            created.firstOrNull { it.path.endsWith("Create${entity}Command.cs") }?.let { open(project, listOf(it.path)) }
        }
    }

    private fun open(project: Project, paths: List<String>) {
        val manager = FileEditorManager.getInstance(project)
        val fs = LocalFileSystem.getInstance()
        paths.mapNotNull { fs.findFileByPath(it) ?: fs.refreshAndFindFileByPath(it) }.forEach { manager.openFile(it, false) }
    }

    private fun String.toHtml(): String =
        replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;").replace("\n", "<br/>")
}
