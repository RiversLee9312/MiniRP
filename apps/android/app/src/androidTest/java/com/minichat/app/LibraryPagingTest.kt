package com.minichat.app

import androidx.paging.PagingConfig
import androidx.paging.PagingSource.LoadParams
import androidx.paging.PagingSource.LoadResult
import androidx.paging.PagingState
import com.minichat.app.data.LibraryPagingSource
import com.minichat.app.ui.Choice
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Test

class LibraryPagingTest {
    @Test
    fun refreshIncludesTheVisibleRowAndCanLoadInBothDirections() = runBlocking {
        val choices = List(300) { Choice("chat-$it", "Conversation $it") }
        val source = LibraryPagingSource { offset, limit -> choices.drop(offset).take(limit) }
        val current = source.load(LoadParams.Refresh(120, 90, false)) as LoadResult.Page
        val state =
            PagingState(
                pages = listOf(current),
                anchorPosition = 40,
                config = PagingConfig(pageSize = 30, enablePlaceholders = false),
                leadingPlaceholderCount = 0,
            )
        val key = source.getRefreshKey(state)
        assertEquals(115, key)
        val refreshed = source.load(LoadParams.Refresh(key, 90, false)) as LoadResult.Page
        assertEquals("chat-160", refreshed.data[45].id)
        val previous =
            source.load(LoadParams.Prepend(requireNotNull(refreshed.prevKey), 30, false))
                as LoadResult.Page
        val next =
            source.load(LoadParams.Append(requireNotNull(refreshed.nextKey), 30, false))
                as LoadResult.Page
        assertEquals(choices.subList(85, 115), previous.data)
        assertEquals(choices.subList(205, 235), next.data)
    }
}
