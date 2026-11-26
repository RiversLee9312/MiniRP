/* SPDX-License-Identifier: LGPL-2.1-or-later */
#include <errno.h>
#include <stdlib.h>
#include <string.h>

#include "libavutil/mem.h"
#include "asset_io.h"
#include "asset_paths.h"

#define MAX_ASSET_BYTES (128LL * 1024 * 1024)

#ifdef _WIN32
#define seek_file _fseeki64
#define tell_file _ftelli64
#else
#define seek_file fseeko
#define tell_file ftello
#endif

FILE *minirp_asset_fopen(const char *url, const char *mode)
{
    if (!strncmp(url, "file:", 5))
        url += 5;
    const char *path = minirp_asset_path(url, strlen(url), mode[0] != 'r' || strchr(mode, '+'));
    return path ? fopen(path, mode) : NULL;
}

static int read_asset(void *handle, uint8_t *buffer, int size)
{
    FILE *file = handle;
    size_t count = fread(buffer, 1, size, file);
    return count ? (int)count : feof(file) ? AVERROR_EOF : AVERROR(EIO);
}

static int write_asset(void *handle, const uint8_t *buffer, int size)
{
    FILE *file = handle;
    int64_t position = tell_file(file);
    if (position < 0 || size > MAX_ASSET_BYTES - position)
        return AVERROR(EFBIG);
    return fwrite(buffer, 1, size, file) == size ? size : AVERROR(EIO);
}

static int64_t seek_asset(void *handle, int64_t offset, int whence)
{
    FILE *file = handle;
    whence &= ~AVSEEK_FORCE;
    if (whence == AVSEEK_SIZE) {
        int64_t current = tell_file(file);
        if (current < 0 || seek_file(file, 0, SEEK_END))
            return AVERROR(EIO);
        int64_t size = tell_file(file);
        return seek_file(file, current, SEEK_SET) || size < 0 ? AVERROR(EIO) : size;
    }
    if (whence != SEEK_SET && whence != SEEK_CUR && whence != SEEK_END)
        return AVERROR(EINVAL);
    if (offset < -MAX_ASSET_BYTES || offset > MAX_ASSET_BYTES || seek_file(file, offset, whence))
        return AVERROR(EIO);
    const int64_t position = tell_file(file);
    return position >= 0 && position <= MAX_ASSET_BYTES ? position : AVERROR(EIO);
}

int minirp_asset_open(AVIOContext **io, const char *url, int flags,
                      const AVIOInterruptCB *interrupt, AVDictionary **options)
{
    const int write = (flags & AVIO_FLAG_WRITE) != 0;
    if (!strcmp(url, "pipe:") || !strcmp(url, write ? "pipe:1" : "pipe:0"))
        return avio_open2(io, url, flags, interrupt, options);

    FILE *file = minirp_asset_fopen(url, write ? "w+b" : "rb");
    if (!file)
        return AVERROR(errno);
    const int size = 64 * 1024;
    uint8_t *buffer = av_malloc(size);
    if (buffer) {
        *io = avio_alloc_context(buffer, size, write, file, write ? NULL : read_asset,
                                 write ? write_asset : NULL, seek_asset);
        if (*io)
            return 0;
    }
    av_free(buffer);
    fclose(file);
    return AVERROR(ENOMEM);
}

int minirp_asset_close(AVIOContext *io)
{
    if (!io)
        return 0;
    if (io->seek != seek_asset)
        return avio_close(io);
    if (io->write_flag)
        avio_flush(io);
    int error = io->error;
    if (fclose(io->opaque) && error >= 0)
        error = AVERROR(EIO);
    av_freep(&io->buffer);
    avio_context_free(&io);
    return error;
}

int minirp_asset_closep(AVIOContext **io)
{
    int error = minirp_asset_close(*io);
    *io = NULL;
    return error;
}

int minirp_asset_open_context(AVFormatContext *ctx, AVIOContext **io, const char *url,
                              int flags, AVDictionary **options)
{
    return minirp_asset_open(io, url, flags, &ctx->interrupt_callback, options);
}

int minirp_asset_close_context(AVFormatContext *ctx, AVIOContext *io)
{
    return minirp_asset_close(io);
}
