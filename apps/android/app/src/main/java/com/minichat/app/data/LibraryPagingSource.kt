package com.minichat.app.data

import androidx.paging.PagingSource
import androidx.paging.PagingState
import com.minichat.app.ui.Choice
import kotlinx.coroutines.CancellationException

class LibraryPagingSource(private val page: suspend (Int, Int) -> List<Choice>) :
    PagingSource<Int, Choice>() {
    override suspend fun load(params: LoadParams<Int>): LoadResult<Int, Choice> {
        val offset = params.key ?: 0
        return try {
            val rows = page(offset, params.loadSize)
            LoadResult.Page(
                data = rows,
                prevKey = null,
                nextKey = (offset + rows.size).takeIf { rows.size == params.loadSize },
            )
        } catch (cancelled: CancellationException) {
            throw cancelled
        } catch (error: Exception) {
            LoadResult.Error(error)
        }
    }

    override fun getRefreshKey(state: PagingState<Int, Choice>): Int? = null
}
