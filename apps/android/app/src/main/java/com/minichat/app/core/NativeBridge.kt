package com.minichat.app.core

internal object NativeBridge {
    init {
        System.loadLibrary("minichatbridge")
    }

    external fun create(root: ByteArray, seedDefaultPreset: Boolean): Long

    external fun dispatch(handle: Long, json: ByteArray): ByteArray

    external fun poll(handle: Long): ByteArray

    external fun takeBinary(handle: Long, nonce: ByteArray): ByteArray?

    external fun writeBinary(handle: Long, nonce: ByteArray, value: ByteArray): Boolean

    external fun destroy(handle: Long)
}
