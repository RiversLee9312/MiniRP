/* SPDX-License-Identifier: LGPL-2.1-or-later */
#ifndef MINIRP_MEDIA_ASSET_PATHS_H
#define MINIRP_MEDIA_ASSET_PATHS_H

#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static inline const char *minirp_asset_path(const char *url, size_t length, int write)
{
    char key[40];
    for (int i = 0; i < 16; ++i) {
        snprintf(key, sizeof(key), "MINIRP_MEDIA_URL_%d", i);
        const char *binding = getenv(key);
        if (!binding)
            break;
        if (strlen(binding) != length || memcmp(url, binding, length))
            continue;
        snprintf(key, sizeof(key), "MINIRP_MEDIA_WRITE_%d", i);
        const char *writable = getenv(key);
        if (write && (!writable || strcmp(writable, "1")))
            break;
        snprintf(key, sizeof(key), "MINIRP_MEDIA_FILE_%d", i);
        const char *path = getenv(key);
        if (path)
            return path;
        break;
    }
    errno = EACCES;
    return NULL;
}

#endif
