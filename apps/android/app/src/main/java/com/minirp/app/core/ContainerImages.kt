package com.minirp.app.core

import android.content.Context
import android.system.Os
import java.io.File
import java.io.InputStream
import java.security.MessageDigest

internal class ContainerImages(private val context: Context) {
    private val names = listOf("kernel", "initrd.gz", "root.squashfs")
    private val prefix = "container-image"
    private val sums by lazy {
        context.assets.open("$prefix/SHA256SUMS").use { it.readBytes() }.also {
            check(it.size in 1..1024) { "Invalid container image manifest." }
        }
    }
    val id: String by lazy { hash(sums.inputStream()) }
    val supportsRoomVm: Boolean by lazy { "ROOM_VM" in context.assets.list(prefix).orEmpty() }
    val supportsMicroVm: Boolean by lazy {
        "MICROVM" in context.assets.list(prefix).orEmpty() &&
            "qboot.rom" in context.assets.list("container-firmware").orEmpty()
    }

    fun verify() {
        val expected = sums.toString(Charsets.US_ASCII).lineSequence().filter(String::isNotEmpty).toList()
        check(expected.size == names.size) { "Invalid container image manifest." }
        names.forEachIndexed { index, name ->
            val actual = openBootAsset(name).use(::hash)
            check(expected[index] == "$actual  $name") { "Container image checksum mismatch." }
        }
    }

    fun bootFile(name: String): File {
        require(name == "kernel" || name == "initrd.gz")
        val target = context.codeCacheDir.resolve("container-$id").apply { mkdirs() }.resolve(name)
        if (!target.exists()) {
            val stage = File.createTempFile(name, ".tmp", target.parentFile)
            try {
                openBootAsset(name).use { input ->
                    stage.outputStream().use { output -> input.copyTo(output) }
                }
                check(stage.renameTo(target)) { "Could not prepare the container boot files." }
            } finally {
                stage.delete()
            }
        }
        return target
    }

    // SquashFS is already compressed. Its stored ZIP member is the read-only VM block device.
    fun rootDevice(): String = context.assets.openFd("$prefix/root.squashfs").use { asset ->
        val path = Os.readlink("/proc/self/fd/${asset.parcelFileDescriptor.fd}").replace(",", ",,")
        check(asset.startOffset >= 0 && asset.length > 0 && asset.length % 512 == 0L)
        "driver=raw,file.driver=file,file.filename=$path,file.read-only=on," +
            "offset=${asset.startOffset},size=${asset.length},read-only=on,node-name=minirp-root"
    }

    private fun openBootAsset(name: String): InputStream =
        context.assets.open("$prefix/" + if (name == "initrd.gz") "initrd.img" else name)

    private fun hash(input: InputStream): String {
        val digest = MessageDigest.getInstance("SHA-256")
        val buffer = ByteArray(65536)
        while (true) {
            val size = input.read(buffer)
            if (size < 0) break
            digest.update(buffer, 0, size)
        }
        return digest.digest().joinToString("") { "%02x".format(it) }
    }
}
