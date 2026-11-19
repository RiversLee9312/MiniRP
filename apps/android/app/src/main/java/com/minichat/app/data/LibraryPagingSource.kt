package com.minichat.app.data

import androidx.paging.PagingSource
import androidx.paging.PagingState
import com.minichat.app.ui.Choice
import kotlinx.coroutines.CancellationException

class LibraryPagingSource(private val page: suspend (Int, Int) -> List<Choice>) :
    PagingSource<Int, Choice>() {
    override suspend fun load(params: LoadParams<Int>): LoadResult<Int, Choice> {
        val end = (params as? LoadParams.Prepend)?.key
        val offset = end?.let { (it - params.loadSize).coerceAtLeast(0) } ?: params.key ?: 0
        val limit = end?.minus(offset) ?: params.loadSize
        return try {
            val rows = page(offset, limit)
            LoadResult.Page(
                data = rows,
                prevKey = offset.takeIf { it > 0 },
                nextKey = (offset + rows.size).takeIf { rows.size == limit },
                itemsBefore = offset,
            )
        } catch (cancelled: CancellationException) {
            throw cancelled
        } catch (error: Exception) {
            LoadResult.Error(error)
        }
    }

    override fun getRefreshKey(state: PagingState<Int, Choice>): Int? {
        val anchor = state.anchorPosition ?: return null
        val row = state.closestItemToPosition(anchor) ?: return null
        val page = state.closestPageToPosition(anchor) ?: return null
        val offset = page.itemsBefore + page.data.indexOf(row)
        return (offset - state.config.initialLoadSize / 2).coerceAtLeast(0)
    }
}
