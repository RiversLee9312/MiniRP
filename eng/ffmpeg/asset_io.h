/* SPDX-License-Identifier: LGPL-2.1-or-later */
#pragma once

#include <stdio.h>
#include "libavformat/avformat.h"

int minirp_asset_open(AVIOContext **io, const char *url, int flags,
                      const AVIOInterruptCB *interrupt, AVDictionary **options);
int minirp_asset_close(AVIOContext *io);
int minirp_asset_closep(AVIOContext **io);
int minirp_asset_open_context(AVFormatContext *ctx, AVIOContext **io, const char *url,
                              int flags, AVDictionary **options);
int minirp_asset_close_context(AVFormatContext *ctx, AVIOContext *io);
FILE *minirp_asset_fopen(const char *url, const char *mode);
