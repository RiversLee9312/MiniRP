package com.minirp.app.ui

import android.content.Intent
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.material3.SnackbarHostState
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.platform.LocalContext
import com.minirp.app.R
import java.io.InputStream
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONObject

internal fun isDisplayAssetUrl(url: String): Boolean =
    url.length <= 512 && DisplayAssetUrl.matches(url)

private val DisplayAssetUrl = Regex("minirp://assets/[a-z0-9._]+/[A-Za-z0-9_.-]+")

internal fun interface AssetDownloader {
    fun download(request: JSONObject)
}

internal class DisplayAssets(
    private val downloader: AssetDownloader,
    private val preset: String,
    private val room: String,
    private val conversation: String,
    private val open: suspend (JSONObject, (String) -> Boolean) -> Pair<String, InputStream>?,
) {
    constructor(model: ChatViewModel, downloader: AssetDownloader, preset: String, room: String, conversation: String = "") :
        this(downloader, preset, room, conversation, model::openDisplayMedia)
    private fun request(block: String, url: String) = JSONObject().put("op", "display_asset_open")
        .put("preset", preset).put("room", room).put("conversation", conversation)
        .put("block", block).put("url", url)

    fun download(block: String, url: String) = downloader.download(request(block, url))

    suspend fun openMedia(block: String, url: String): Pair<String, InputStream>? =
        open(request(block, url)) { it in MediaTypes }

    private companion object {
        val MediaTypes = setOf("image/png", "image/jpeg", "image/gif", "image/webp", "image/avif",
            "image/bmp", "image/x-icon")
    }
}

@Composable
internal fun rememberAssetDownloader(model: ChatViewModel, snackbars: SnackbarHostState): AssetDownloader {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var pending by rememberSaveable { mutableStateOf<String?>(null) }
    var preparing by remember { mutableStateOf(false) }
    suspend fun showFailure() = snackbars.showSnackbar(context.getString(R.string.asset_download_failed))

    val picker = rememberLauncherForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
        val request = pending
        pending = null
        val destination = result.data?.data
        if (request != null && destination != null) scope.launch {
            try {
                withContext(Dispatchers.IO) {
                    val output = checkNotNull(context.contentResolver.openOutputStream(destination, "w"))
                    output.use { model.saveDisplayAsset(JSONObject(request), it) }
                }
            } catch (error: CancellationException) {
                throw error
            } catch (_: Exception) {
                showFailure()
            }
        }
    }
    return remember(model, snackbars, scope, picker) {
        AssetDownloader { request ->
            if (pending == null && !preparing) {
                preparing = true
                scope.launch {
                    try {
                        val info = model.displayAssetInfo(request)
                        val name = info.optString("name").filterNot { it.isISOControl() || it in "\\/:*?\"<>|" }
                            .take(180).trim().trim('.').ifBlank { "asset" }
                        val intent = Intent(Intent.ACTION_CREATE_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE)
                            .setType(info.optString("mimeType").substringBefore(';').ifBlank { "application/octet-stream" })
                            .putExtra(Intent.EXTRA_TITLE, name)
                        pending = request.toString()
                        picker.launch(intent)
                    } catch (error: CancellationException) {
                        throw error
                    } catch (_: Exception) {
                        pending = null
                        showFailure()
                    } finally {
                        preparing = false
                    }
                }
            }
        }
    }
}
