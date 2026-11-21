/* Included by curlx/win32-fopen.h after the platform file functions are defined. */
#include <errno.h>
#include <stdarg.h>
#include <string.h>

static inline const char *minirp_asset_path(const char *url, int writing)
{
  int i;
  char key[40];
  if(url && url[0] == '/')
    ++url; /* libcurl's file protocol provides a leading slash. */
  if(!url || (strncmp(url, "minirp://assets/", 16) &&
              strncmp(url, "minichat://assets/", 18))) {
    errno = EACCES;
    return NULL;
  }
  for(i = 0; i < 16; ++i) {
    const char *registered;
    snprintf(key, sizeof(key), "MINIRP_CURL_URL_%d", i);
    registered = getenv(key);
    if(!registered || strcmp(url, registered))
      continue;
    if(writing) {
      const char *access;
      snprintf(key, sizeof(key), "MINIRP_CURL_WRITE_%d", i);
      access = getenv(key);
      if(!access || strcmp(access, "1"))
        break;
    }
    snprintf(key, sizeof(key), "MINIRP_CURL_FILE_%d", i);
    return getenv(key);
  }
  errno = EACCES;
  return NULL;
}

static inline const char *minirp_asset_path_or_empty(const char *url, int writing)
{
  const char *path = minirp_asset_path(url, writing);
  return path ? path : "";
}

static inline FILE *minirp_fopen(const char *name, const char *mode)
{
  const char *path = minirp_asset_path(name, mode[0] != 'r' || strchr(mode, '+'));
  return path ? CURLX_FOPEN_LOW(path, mode) : NULL;
}

static inline int minirp_open(const char *name, int flags, ...)
{
  const char *path = minirp_asset_path(name, flags & (O_WRONLY | O_RDWR | O_CREAT));
  if(!path)
    return -1;
  if(flags & O_CREAT) {
    int mode;
    va_list args;
    va_start(args, flags);
    mode = va_arg(args, int);
    va_end(args);
    return MINIRP_OPEN_LOW(path, flags, mode);
  }
  return MINIRP_OPEN_LOW(path, flags);
}

static inline int minirp_stat(const char *name, curlx_struct_stat *buffer)
{
  const char *path = minirp_asset_path(name, 0);
  return path ? MINIRP_STAT_LOW(path, buffer) : -1;
}

static inline FILE *minirp_freopen(const char *name, const char *mode, FILE *stream)
{
  const char *path = minirp_asset_path(name, mode[0] != 'r' || strchr(mode, '+'));
  return path ? CURLX_FREOPEN_LOW(path, mode, stream) : NULL;
}

static inline int minirp_mkdir(const char *path, int mode)
{
  (void)path;
  (void)mode;
  errno = EACCES;
  return -1;
}

#define curlx_open minirp_open
#define curlx_stat minirp_stat
#define curlx_mkdir(path, mode) minirp_mkdir(path, mode)
#define curlx_fopen minirp_fopen
#define curlx_freopen minirp_freopen
