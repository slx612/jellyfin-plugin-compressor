#!/usr/bin/env bash
# Disposable synthetic Jellyfin only. Never mount an existing server or real media.
set -euo pipefail
repo=$(cd -- "$(dirname -- "$0")/.." && pwd)
fixture="$repo/artifacts/identity-check/ci-${GITHUB_RUN_ID:-local}-${RANDOM}"
container="compressor-identity-${RANDOM}-${RANDOM}"
image='ghcr.io/jellyfin/jellyfin:10.11.6@sha256:8daa84b40dedbb1307d8958728be583f98334a2b0ae17a8c584fbcb64103b678'
created=false
cleanup() { if "$created"; then docker stop --time 60 "$container" >/dev/null || true; fi; }
trap cleanup EXIT
if curl --silent --fail --max-time 2 http://127.0.0.1:18096/health >/dev/null; then
    echo 'Port 18096 already serves a server; refusing to continue.' >&2
    exit 1
fi
python3 "$repo/scripts/verify-library-state.py" prepare --fixture "$fixture" \
    --ffmpeg "$(command -v ffmpeg)" \
    --plugin "$repo/Jellyfin.Plugin.PreTranscode/bin/Release/net9.0/Jellyfin.Plugin.Compressor.dll"
docker create --name "$container" --platform linux/amd64 --network host \
    --user "$(id -u):$(id -g)" --mount "type=bind,source=$fixture,target=$fixture" \
    "$image" --datadir "$fixture/server" --configdir "$fixture/server/config" \
    --cachedir "$fixture/server/cache" --logdir "$fixture/server/log" \
    --ffmpeg /usr/lib/jellyfin-ffmpeg/ffmpeg --nowebclient --nonetchange >/dev/null
created=true
start_server() {
    docker start "$container" >/dev/null
    for attempt in {1..90}; do
        if [ "$(curl --silent --max-time 2 http://127.0.0.1:18096/health || true)" = 'Healthy' ]; then return; fi
        sleep 2
    done
    docker logs --tail 80 "$container" >&2
    echo 'Synthetic server did not become healthy.' >&2
    exit 1
}
start_server
python3 "$repo/scripts/verify-library-state.py" compress --fixture "$fixture"
docker stop --time 120 "$container" >/dev/null
start_server
python3 "$repo/scripts/verify-library-state.py" after-restart --fixture "$fixture"
cat "$fixture/result.json"
