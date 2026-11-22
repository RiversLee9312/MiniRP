package com.minirp.app.core

import com.caoccao.javet.interop.V8Runtime
import com.caoccao.javet.interop.callback.IV8ModuleResolver
import com.caoccao.javet.values.V8Value
import com.caoccao.javet.values.reference.IV8Module
import com.caoccao.javet.values.reference.V8ValuePromise
import org.json.JSONObject

internal class PluginModules(private val sources: JSONObject) : IV8ModuleResolver {
    fun load(runtime: V8Runtime, entry: String) {
        runtime.v8ModuleResolver = this
        // The runtime caches modules by resource name and owns their references until it closes.
        val module = compile(runtime, entry)
        module.execute<V8Value>().use { evaluation ->
            if (evaluation is V8ValuePromise) {
                check(!evaluation.isPending) { "Plugin modules must finish initializing before handling events." }
                if (evaluation.isRejected) {
                    evaluation.getResult<V8Value>().use { error("Plugin module '$entry' failed: $it") }
                }
            }
        }
    }

    override fun getAbsoluteResourceName(runtime: V8Runtime, resourceName: String, referrer: IV8Module?): String =
        resolvePath(resourceName, referrer?.resourceName.orEmpty())

    override fun resolve(runtime: V8Runtime, resourceName: String, referrer: IV8Module?): IV8Module =
        compile(runtime, resolvePath(resourceName, referrer?.resourceName.orEmpty()))

    private fun compile(runtime: V8Runtime, path: String): IV8Module =
        runtime.getExecutor(sources.getString(path)).setResourceName(path).compileV8Module()

    private fun resolvePath(specifier: String, from: String): String {
        fun unresolved(): Nothing = error(
            "Cannot import '$specifier' from '$from' in this plugin. Use a relative path to a packaged .js or .mjs file."
        )
        if (!sources.has(from) || specifier.length > 1024 ||
            !(specifier.startsWith("./") || specifier.startsWith("../")) ||
            !(specifier.endsWith(".js") || specifier.endsWith(".mjs"))
        ) unresolved()
        val parts = from.split('/').dropLast(1).toMutableList()
        for (part in specifier.split('/')) {
            when {
                part == "." -> Unit
                part == ".." -> if (parts.isEmpty()) unresolved() else parts.removeAt(parts.lastIndex)
                part.isNotEmpty() && part.all { it in 'a'..'z' || it in 'A'..'Z' || it in '0'..'9' || it in ".-_" } ->
                    parts.add(part)
                else -> unresolved()
            }
        }
        return parts.joinToString("/").also { if (!sources.has(it)) unresolved() }
    }
}
