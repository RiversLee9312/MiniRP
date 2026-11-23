package com.minirp.app.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import com.minirp.app.R
import org.json.JSONObject

enum class ResearchService(val id: String, val displayName: String, val canFetch: Boolean = true) {
    DeepSeek("deepseek", "DeepSeek"),
    Tavily("tavily", "Tavily"),
    Exa("exa", "Exa"),
    Brave("brave", "Brave Search", canFetch = false),
}

data class ResearchSettings(
    val search: ResearchService = ResearchService.DeepSeek,
    val fetch: ResearchService = ResearchService.DeepSeek,
    val keys: Map<ResearchService, String> = emptyMap(),
) {
    internal fun encodeKeys() = JSONObject(keys.mapKeys { it.key.id })
}

internal fun decodeResearchSettings(options: JSONObject): ResearchSettings {
    val search = ResearchService.entries.find { it.id == options.optString("search_provider") }
    val fetch = ResearchService.entries.find { it.canFetch && it.id == options.optString("fetch_provider") }
    val keys = options.optJSONObject("research_keys")
    return ResearchSettings(
        search ?: ResearchService.DeepSeek,
        fetch ?: ResearchService.DeepSeek,
        ResearchService.entries.mapNotNull { service ->
            keys?.optString(service.id)?.takeIf(String::isNotBlank)?.let { service to it }
        }.toMap(),
    )
}

@Composable
internal fun ResearchSettingsScreen(settings: ResearchSettings, save: (ResearchSettings) -> Unit) {
    var search by rememberSaveable { mutableStateOf(settings.search) }
    var fetch by rememberSaveable { mutableStateOf(settings.fetch) }
    val keys = remember { mutableStateMapOf<ResearchService, String>().apply { putAll(settings.keys) } }
    LazyColumn(
        Modifier.fillMaxSize(),
        contentPadding = PaddingValues(16.dp),
        verticalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        item {
            ResearchServicePicker(R.string.research_search_provider, "search", search, ResearchService.entries) { search = it }
            ResearchServicePicker(R.string.research_fetch_provider, "browse", fetch, ResearchService.entries.filter { it.canFetch }) {
                fetch = it
            }
        }
        items(listOf(search, fetch).distinct(), key = { it.id }) { service ->
            OutlinedTextField(
                value = keys[service].orEmpty(),
                onValueChange = { keys[service] = it },
                label = { Text("${service.displayName} · ${stringResource(R.string.api_key)}") },
                singleLine = true,
                visualTransformation = PasswordVisualTransformation(),
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
                modifier = Modifier.fillMaxWidth(),
            )
        }
        item {
            Button(onClick = {
                save(ResearchSettings(search, fetch, keys.mapValues { it.value.trim() }.filterValues { it.isNotEmpty() }))
            }) {
                Text(stringResource(R.string.save))
            }
        }
    }
}

@Composable
private fun ResearchServicePicker(
    title: Int,
    icon: String,
    active: ResearchService,
    services: List<ResearchService>,
    choose: (ResearchService) -> Unit,
) {
    var expanded by remember { mutableStateOf(false) }
    Box {
        ListItem(
            content = { Text(stringResource(title)) },
            leadingContent = { Symbol(icon) },
            supportingContent = { Text(active.displayName) },
            trailingContent = { Symbol("expand_more") },
            modifier = Modifier.clickable { expanded = true },
        )
        DropdownMenu(expanded, onDismissRequest = { expanded = false }) {
            services.forEach { service ->
                DropdownMenuItem(
                    text = { Text(service.displayName) },
                    onClick = {
                        choose(service)
                        expanded = false
                    },
                )
            }
        }
    }
}
