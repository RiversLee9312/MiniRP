package com.minirp.app.core

internal object NativeBridge {
    init {
        System.loadLibrary("minirpbridge")
    }

    external fun create(root: ByteArray, seedDefaultPreset: Boolean): Long

    external fun dispatch(handle: Long, json: ByteArray): ByteArray

    external fun poll(handle: Long): ByteArray

    external fun takeBinary(handle: Long, nonce: ByteArray): ByteArray?

    external fun writeBinary(handle: Long, nonce: ByteArray, value: ByteArray): Boolean

    external fun destroy(handle: Long)
}
