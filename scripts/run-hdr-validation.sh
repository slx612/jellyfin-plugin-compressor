#!/usr/bin/env bash
# Run INSIDE the existing Jellyfin container. No install, configuration change or media replacement.
set -euo pipefail
bundle=$(cd -- "$(dirname -- "$0")" && pwd)
case "$bundle" in /config/data/jellyfin-compressor/hdr-validation/*) ;; *) echo 'Unexpected bundle directory' >&2; exit 1;; esac
source=${1:?Pass the exact /Peliculas MKV source path}
run="$bundle/run-$(date -u +%Y%m%dT%H%M%SZ)"
test -f "$source"
test ! -e "$run"
test -x /usr/lib/jellyfin-ffmpeg/ffmpeg
test -x /usr/lib/jellyfin-ffmpeg/ffprobe
cd -- "$bundle"
sha256sum --check --quiet SHA256SUMS
chmod u+x app/Jellyfin.Compressor.HdrValidation tools/hdr10plus_tool tools/dovi_tool tools/MKVToolNix_GUI-102.0-x86_64.AppImage
test -L tools/mkvmerge || ln -s MKVToolNix_GUI-102.0-x86_64.AppImage tools/mkvmerge
test -L tools/mkvextract || ln -s MKVToolNix_GUI-102.0-x86_64.AppImage tools/mkvextract
nohup nice -n 10 "$bundle/app/Jellyfin.Compressor.HdrValidation" "$source" "$run" "$bundle/tools" >"$run.log" 2>&1 < /dev/null &
printf '%s\n' "$!" >"$run.pid"
echo "Diagnostic started: PID $(cat "$run.pid")"
echo "Original: $source (read only; no replacement or quarantine operation)"
echo "Output and checks: $run"
echo "Progress log: $run.log"
sleep 2
if ! kill -0 "$(cat "$run.pid")" 2>/dev/null; then cat "$run.log"; exit 1; fi
tail -n 10 "$run.log"
