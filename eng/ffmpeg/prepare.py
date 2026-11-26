#!/usr/bin/env python3
# SPDX-License-Identifier: LGPL-2.1-or-later
"""Wire Asset I/O into the FFmpeg 8.1.3 CLI and x264."""
import re
import shutil
import sys
from pathlib import Path


scripts = Path(__file__).parent
root = Path(sys.argv[1]) / "fftools"
for name in ("asset_io.c", "asset_io.h", "asset_paths.h"):
    shutil.copyfile(scripts / name, root / name)


def replace(path, changes):
    path = root / path
    text = path.read_text(encoding="utf-8")
    for original, replacement in changes:
        if text.count(original) != 1:
            raise SystemExit(f"Unexpected CLI source for {path.name}: {original}")
        text = text.replace(original, replacement)
    path.write_text(text, encoding="utf-8")


contexts = (
    ("ffmpeg_demux.c", "    if (o->audio_sample_rate.nb_opt) {", "ic"),
    ("ffmpeg_mux_init.c", "    mux->fc = oc;", "oc"),
    ("ffprobe.c", "    err = set_decoders(fmt_ctx);", "fmt_ctx"),
)
for name, marker, ctx in contexts:
    replace(name, [(marker, f"    {ctx}->io_open = minirp_asset_open_context;\n"
                            f"    {ctx}->io_close2 = minirp_asset_close_context;\n{marker}")])

replace("ffmpeg_mux_init.c", [
    ('                    av_opt_set(video_enc, "stats", logfilename,\n'
     '                               AV_OPT_SEARCH_CHILDREN);',
     '                    av_opt_set(video_enc, "stats",\n'
     '                               ost->logfile_prefix && av_strstart(ost->logfile_prefix, "minirp://assets/", NULL) &&\n'
     '                               !strcmp(video_enc->codec->name, "libx264") ? ost->logfile_prefix : logfilename,\n'
     '                               AV_OPT_SEARCH_CHILDREN);'),
])

for path in root.rglob("*.c"):
    if path.name == "asset_io.c":
        continue
    text = path.read_text(encoding="utf-8")
    updated = re.sub(r"\bavio_open\(([^;\n]*?),\s*(AVIO_FLAG_(?:READ|WRITE))\)",
                     r"minirp_asset_open(\1, \2, NULL, NULL)", text)
    for old, new in (
        ("avio_open2", "minirp_asset_open"), ("avio_close", "minirp_asset_close"),
        ("avio_closep", "minirp_asset_closep"), ("fopen_utf8", "minirp_asset_fopen"),
        ("fopen", "minirp_asset_fopen"),
    ):
        updated = re.sub(rf"\b{old}(?=\()", new, updated)
    if updated != text or path.name in {"ffmpeg_demux.c", "ffmpeg_mux_init.c", "ffprobe.c"}:
        updated = updated.replace('#include "config.h"', '#include "config.h"\n#include "fftools/asset_io.h"', 1) \
            if '#include "config.h"' in updated else '#include "fftools/asset_io.h"\n' + updated
        path.write_text(updated, encoding="utf-8")

replace("Makefile", [
    ("fftools/cmdutils.o fftools/opt_common.o", "fftools/asset_io.o fftools/cmdutils.o fftools/opt_common.o"),
])

common = Path(sys.argv[2]) / "common"
shutil.copyfile(scripts / "asset_paths.h", common / "minirp_asset_paths.h")
shutil.copyfile(scripts / "x264_asset_io.h", common / "minirp_x264_asset_io.h")
replace(common.resolve() / "osdep.h", [
    ("/* mdate: return the current date in microsecond */",
     '#include "minirp_x264_asset_io.h"\n\n/* mdate: return the current date in microsecond */'),
])
