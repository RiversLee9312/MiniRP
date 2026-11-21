"""Bind the pinned upstream curl tool's file operations to its MiniRP Asset table."""

import pathlib
import re
import sys


source = pathlib.Path(sys.argv[1])
here = pathlib.Path(__file__).resolve().parent


def edit(name, transform):
    file = source / name
    previous = file.read_text(encoding="utf-8")
    changed = transform(previous)
    if changed == previous:
        raise ValueError(f"Expected upstream file ingress in {name}")
    file.write_text(changed, encoding="utf-8")


def fopen_header(text):
    text = re.sub(r"(?m)^#define curlx_(open|stat)\b",
                  lambda match: "#define MINIRP_" + match[1].upper() + "_LOW", text)
    text = re.sub(r"(?m)^#define curlx_mkdir[^\n]*\n", "", text)
    text = re.sub(r"(?m)^#define curlx_(fopen|freopen)\s+CURLX_[A-Z_]+$", "", text)
    return text.replace("#endif /* HEADER_CURLX_FOPEN_H */",
                        '#include "curlx/minirp-asset-files.h"\n\n#endif /* HEADER_CURLX_FOPEN_H */')


header = source / "lib/curlx/minirp-asset-files.h"
header.write_bytes((here / "asset-files.h").read_bytes())
if 'minirp-asset-files.h' not in (source / "lib/curlx/win32-fopen.h").read_text():
    edit("lib/curlx/win32-fopen.h", fopen_header)
    edit("lib/curl_fopen.c", lambda text: text[:text.index("#ifdef _WIN32\n#define PATHSEP")] + """
CURLcode Curl_fopen(struct Curl_easy *data, const char *filename,
                    FILE **fh, char **tempname)
{
  (void)data;
  *tempname = NULL;
  *fh = curlx_fopen(filename, FOPEN_WRITETEXT);
  return *fh ? CURLE_OK : CURLE_WRITE_ERROR;
}

#endif
""")

    # TLS reads delegated to Mbed TLS are outside curlx. Resolve the Asset
    # before handing the path to the TLS backend. Trust defaults are embedded.
    def mbed_paths(text):
        if '#include "curlx/win32-fopen.h"' not in text:
            text = text.replace('#include "curl_setup.h"', '#include "curl_setup.h"\n#include "curlx/win32-fopen.h"')
        for value in ("ssl_cafile", "ssl_cert", "conn_config->key", "ssl_crlfile"):
            text = re.sub(r"(mbedtls_[a-z0-9_]+(?:parse_file|parse_keyfile)\([^,]+,\s*)" + re.escape(value) + r"(?=[,)])",
                          lambda match: match[1] + f'minirp_asset_path_or_empty({value}, 0)', text)
        return text.replace("mbedtls_x509_crt_parse_path(&backend->cacert, ssl_capath)", "MBEDTLS_ERR_X509_FILE_IO_ERROR")

    edit("lib/vtls/mbedtls.c", mbed_paths)
    edit("src/tool_operate.c", lambda text: text.replace("if(!unlink(outs->filename))", "if(minirp_asset_path(outs->filename, 1) &&\n           !unlink(minirp_asset_path(outs->filename, 1)))"))
    edit("src/tool_filetime.c", lambda text: text.replace("curlx_CreateFile(filename, FILE_READ_ATTRIBUTES", "curlx_CreateFile(minirp_asset_path_or_empty(filename, 0), FILE_READ_ATTRIBUTES")
         .replace("curlx_CreateFile(filename, FILE_WRITE_ATTRIBUTES", "curlx_CreateFile(minirp_asset_path_or_empty(filename, 1), FILE_WRITE_ATTRIBUTES")
         .replace("if(utime(filename, &times))", "if(!minirp_asset_path(filename, 1) || utime(minirp_asset_path(filename, 1), &times))"))

    def file_protocol(text):
        start = text.index("#ifdef DOS_FILESYSTEM\n  /* If the first character is a slash")
        end = text.index("  curlx_free(file->freepath);", start)
        text = text[:start] + """  fd = curlx_open(real_path, O_RDONLY | CURL_O_BINARY);
  file->path = real_path;
""" + text[end:]
        return text.replace("DIR *dir = opendir(file->path);", "DIR *dir = NULL; /* Assets are regular files. */") \
            .replace("handle = curlx_FindFirstFile(pattern, &entry);", "handle = INVALID_HANDLE_VALUE; /* Assets are regular files. */") \
            .replace("#ifdef DOS_FILESYSTEM\n  size_t i;\n  char *actual_path;\n#endif\n", "")

    edit("lib/file.c", file_protocol)

    def ssh_paths(text):
        if '#include "curlx/win32-fopen.h"' not in text:
            text = text.replace('#include "curl_setup.h"', '#include "curl_setup.h"\n#include "curlx/win32-fopen.h"')
        text = text.replace("sshc->pub_key, sshc->priv_key,", "minirp_asset_path(sshc->pub_key, 0), minirp_asset_path_or_empty(sshc->priv_key, 0),")
        text = re.sub(r"(libssh2_knownhost_(read|write)file\(\s*sshc->kh, )CURL_EASY_STR\(data, STRING_SSH_KNOWNHOSTS\)",
                      lambda match: match[1] + "minirp_asset_path_or_empty(CURL_EASY_STR(data, STRING_SSH_KNOWNHOSTS), " +
                      ("1" if match[2] == "write" else "0") + ")", text)
        return text.replace("rc = libssh2_agent_connect(sshc->ssh_agent);", "rc = LIBSSH2_ERROR_AGENT_PROTOCOL;")

    edit("lib/vssh/libssh2.c", ssh_paths)
