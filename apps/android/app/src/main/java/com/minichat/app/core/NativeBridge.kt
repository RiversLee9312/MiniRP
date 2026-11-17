package com.minichat.app.core

internal object NativeBridge {
    init {
        System.loadLibrary("minichatbridge")
    }

    external fun create(root: ByteArray): Long

    external fun dispatch(handle: Long, json: ByteArray): ByteArray

    external fun poll(handle: Long): ByteArray

    external fun destroy(handle: Long)
}
