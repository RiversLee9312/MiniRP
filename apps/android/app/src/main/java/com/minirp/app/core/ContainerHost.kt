package com.minirp.app.core

import android.content.Context
import android.net.ConnectivityManager
import java.io.File
import kotlinx.coroutines.delay
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import org.json.JSONObject

internal class ContainerHost(private val context: Context, private val workspace: File) {
    private val gate = Mutex()
    private val images = ContainerImages(context)
    private val executable = File(context.applicationInfo.nativeLibraryDir, "libminirp_qemu.so")
    private var verified = false
    fun available(): Boolean = executable.isFile && images.supportsRoomVm

    suspend fun handle(event: JSONObject): JSONObject = gate.withLock {
        check(executable.isFile) { "Install the MiniRP build with the container runtime." }
        val id = images.id
        check(event.optString("image").ifBlank { id } == id) { "This workspace was created with a different container image." }
        if (!verified) {
            images.verify()
            verified = true
        }
        if (event.getString("kind") == "container_runtime") return JSONObject().put("image", id)
            .put("room_vm", images.supportsRoomVm)
            .put("machine", if (images.supportsMicroVm) "microvm" else "q35")
        val run = event.getString("run")
        require(run.matches(Regex("[0-9a-f]{12}")))
        val directory = workspace.resolve("container-runs").resolve(run)
        val manager = context.getSystemService(ConnectivityManager::class.java)
        val dns = manager?.getLinkProperties(manager.activeNetwork)?.dnsServers.orEmpty()
        directory.resolve("resolv.conf").writeText(dns.joinToString("") { "nameserver ${it.hostAddress}\n" })
        val firmware = context.codeCacheDir.resolve("container-firmware").apply { mkdirs() }
        for (name in context.assets.list("container-firmware").orEmpty()) {
            val destination = firmware.resolve(name)
            if (destination.isFile) continue
            val stage = File.createTempFile(name, ".tmp", firmware)
            try {
                context.assets.open("container-firmware/$name").use { input ->
                    stage.outputStream().use { input.copyTo(it) }
                }
                check(stage.renameTo(destination)) { "Could not prepare QEMU firmware." }
            } finally {
                stage.delete()
            }
        }
        val args = mutableListOf(executable.path, "-L", firmware.path, "-accel", "tcg,tb-size=32",
            "-run-with", "exit-with-parent=on")
        val requested = event.getJSONArray("arguments")
        repeat(requested.length()) { index ->
            when (val argument = requested.getString(index)) {
                "{kernel}" -> args += images.bootFile("kernel").path
                "{initrd}" -> args += images.bootFile("initrd.gz").path
                "file={rootfs},if=none,id=minirp-root,format=raw,readonly=on" -> {
                    check(args.removeAt(args.lastIndex) == "-drive")
                    args += listOf("-blockdev", images.rootDevice())
                }
                else -> args += argument
            }
        }
        val log = directory.resolve("qemu.log")
        val process = ProcessBuilder(args).directory(directory).redirectErrorStream(true).redirectOutput(log)
            .apply {
                environment()["LD_LIBRARY_PATH"] = executable.parentFile!!.path
                environment()["TMPDIR"] = directory.path
            }.start()
        try {
            while (process.isAlive) delay(100)
            check(process.exitValue() == 0) {
                "QEMU failed: " + log.reader().use { reader ->
                    val chars = CharArray(1024)
                    val count = reader.read(chars)
                    if (count > 0) String(chars, 0, count).trim() else ""
                }
            }
        } finally {
            if (process.isAlive) {
                process.destroyForcibly()
                process.waitFor()
            }
        }
        JSONObject()
    }

}
