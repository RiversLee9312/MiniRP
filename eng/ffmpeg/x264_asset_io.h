/* SPDX-License-Identifier: GPL-2.0-or-later */
#include "minirp_asset_paths.h"

/* x264 writes a pass log to a .temp file before replacing the bound Asset. */
static inline const char *minirp_x264_path(const char *url, int write, char *temp, size_t size)
{
    size_t length = strlen(url);
    const size_t suffix = sizeof(".temp") - 1;
    const char *path = minirp_asset_path(url, length, write);
    if (path || length <= suffix || strcmp(url + length - suffix, ".temp"))
        return path;
    path = minirp_asset_path(url, length - suffix, 1);
    if (!path)
        return NULL;
    int written = snprintf(temp, size, "%s.temp", path);
    if (written < 0 || (size_t)written >= size) {
        errno = ENAMETOOLONG;
        return NULL;
    }
    return temp;
}

static inline FILE *minirp_x264_fopen(const char *url, const char *mode)
{
    char temp[64];
    const char *path = minirp_x264_path(url, mode[0] != 'r' || strchr(mode, '+'), temp, sizeof(temp));
    return path ? fopen(path, mode) : NULL;
}

static inline int minirp_x264_rename(const char *oldurl, const char *newurl)
{
    char temp[64];
    const char *oldpath = minirp_x264_path(oldurl, 1, temp, sizeof(temp));
    const char *newpath = minirp_asset_path(newurl, strlen(newurl), 1);
    if (!oldpath || !newpath)
        return -1;
#ifdef _WIN32
    if (MoveFileExA(oldpath, newpath, MOVEFILE_REPLACE_EXISTING))
        return 0;
    errno = EACCES;
    return -1;
#else
    return rename(oldpath, newpath);
#endif
}

static inline int minirp_x264_stat(const char *url, x264_struct_stat *info)
{
    char temp[64];
    const char *path = minirp_x264_path(url, 0, temp, sizeof(temp));
    return path ? x264_stat(path, info) : -1;
}

#undef x264_fopen
#undef x264_rename
#undef x264_stat
#define x264_fopen minirp_x264_fopen
#define x264_rename minirp_x264_rename
#define x264_stat minirp_x264_stat
