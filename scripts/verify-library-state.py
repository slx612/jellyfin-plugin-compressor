"""Opt-in live Jellyfin 10.11.6 regression check, using synthetic media only.

Run through verify-library-state.ps1. All evidence and disposable server state
stay in artifacts/identity-check/<run>. No third-party Python dependencies.
"""

import argparse
import hashlib
import json
import os
from pathlib import Path
import secrets
import shutil
import subprocess
import time
import urllib.error
import urllib.parse
import urllib.request


BASE = "http://127.0.0.1:18096"
USER_FIELDS = ("Key", "Played", "PlaybackPositionTicks", "PlayCount", "LastPlayedDate", "IsFavorite")
ITEM_FIELDS = ("Id", "Path", "DateCreated")


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def save(path, value):
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False), encoding="utf-8")


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def wait_for(action, description, seconds=180):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        result = action()
        if result:
            return result
        time.sleep(2)
    raise RuntimeError("Timeout: " + description)


class Api:
    def __init__(self, device="admin"):
        self.device = device
        self.token = None
        self.user_id = None
        self.http = urllib.request.build_opener(urllib.request.ProxyHandler({}))

    def call(self, path, method="GET", body=None, **query):
        suffix = "?" + urllib.parse.urlencode(query) if query else ""
        auth = ('MediaBrowser Client="Compressor identity test", Device="Synthetic fixture", '
                f'DeviceId="compressor-identity-{self.device}", Version="1.0"')
        if self.token:
            auth += f', Token="{self.token}"'
        data = json.dumps(body).encode() if body is not None else None
        request = urllib.request.Request(BASE + path + suffix, data=data, method=method,
                                         headers={"Authorization": auth, "Content-Type": "application/json"})
        try:
            with self.http.open(request, timeout=90) as response:
                content = response.read()
                return json.loads(content) if content else None
        except urllib.error.HTTPError as error:
            raise RuntimeError(f"{method} {path}: HTTP {error.code}: {error.read().decode()[:1500]}") from error

    def login(self, name, password):
        session = self.call("/Users/AuthenticateByName", "POST", {"Username": name, "Pw": password})
        self.token = session["AccessToken"]
        self.user_id = session["User"]["Id"]


def prepare(root, ffmpeg, plugin):
    require(not root.exists(), "Use a NEW fixture directory; refusing to overwrite a prior run.")
    root.mkdir(parents=True)
    for name in ("server/config", "server/cache", "server/log", "server/plugins/Compressor_0.1.0.0", "media", "originals"):
        (root / name).mkdir(parents=True)
    (root / "server/config/network.xml").write_text('''<?xml version="1.0" encoding="utf-8"?>
<NetworkConfiguration>
  <InternalHttpPort>18096</InternalHttpPort><PublicHttpPort>18096</PublicHttpPort>
  <EnableIPv4>true</EnableIPv4><EnableIPv6>false</EnableIPv6>
  <EnableRemoteAccess>false</EnableRemoteAccess><AutoDiscovery>false</AutoDiscovery>
  <LocalNetworkAddresses><string>127.0.0.1</string></LocalNetworkAddresses>
</NetworkConfiguration>
''', encoding="utf-8")
    shutil.copy2(plugin, root / "server/plugins/Compressor_0.1.0.0/Jellyfin.Plugin.Compressor.dll")
    files = []
    for index, extension in enumerate(("mkv", "mp4", "m4v")):
        path = root / "media" / f"Identity fixture {index + 1}.{extension}"
        command = [ffmpeg, "-hide_banner", "-loglevel", "error", "-nostdin", "-n",
                   "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=5",
                   "-f", "lavfi", "-i", f"sine=frequency={440 + index * 100}:sample_rate=48000",
                   "-t", "600", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "0",
                   "-pix_fmt", "yuv420p", "-threads", "2", "-c:a", "aac", "-b:a", "32k",
                   "-f", "matroska" if extension == "mkv" else "mp4", str(path)]
        subprocess.run(command, check=True, timeout=120)
        stamp = time.time() - (index + 1) * 86400
        os.utime(path, (stamp, stamp))
        files.append({"Path": str(path), "Sha256": digest(path), "Size": path.stat().st_size})
    save(root / "fixture.json", {"Files": files, "Password": secrets.token_urlsafe(24),
                                 "PluginSha256": digest(Path(plugin))})
    print(f"Prepared 3 synthetic 10-minute films: {root}", flush=True)


def scan(api):
    tasks = api.call("/ScheduledTasks")
    task = next(t for t in tasks if t["Key"] == "RefreshLibrary")
    if task["State"] != "Idle":
        wait_for(lambda: api.call("/ScheduledTasks/" + task["Id"])["State"] == "Idle",
                 "existing library scan", 300)
        task = api.call("/ScheduledTasks/" + task["Id"])
    # Use the task's completion timestamp to avoid accepting Idle before it starts.
    previous = task.get("LastExecutionResult")
    api.call("/ScheduledTasks/Running/" + task["Id"], "POST")

    def finished():
        current = api.call("/ScheduledTasks/" + task["Id"])
        result = current.get("LastExecutionResult")
        if current["State"] == "Idle" and result and result != previous:
            require(result["Status"] == "Completed", f"Scan failed: {result}")
            return True
        return False

    wait_for(finished, "library scan", 300)


def movies(api):
    return api.call("/Items", userId=api.user_id, recursive="true", includeItemTypes="Movie",
                    fields="Path,DateCreated,MediaStreams", enableUserData="true",
                    sortBy="DateCreated", sortOrder="Descending")["Items"]


def snapshot(users, library_id):
    result = {}
    for name, api in users.items():
        items = movies(api)
        require(len(items) == 3, f"{name}: expected exactly 3 movies, got {len(items)}")
        rows = []
        for item in items:
            require(all(k in item for k in ITEM_FIELDS), "Incomplete item data")
            data = item["UserData"]
            require(all(k in data for k in ("Key", "Played", "PlaybackPositionTicks", "PlayCount", "IsFavorite")),
                    "Incomplete user data")
            rows.append({**{k: item[k] for k in ITEM_FIELDS},
                         "UserData": {k: data.get(k) for k in USER_FIELDS}})
        common = {"userId": api.user_id, "parentId": library_id, "includeItemTypes": "Movie", "limit": 100}
        latest = api.call("/Items/Latest", **common, groupItems="false")
        unplayed = api.call("/Items/Latest", **common, groupItems="false", isPlayed="false")
        resume = api.call("/UserItems/Resume", **common)["Items"]
        result[name] = {"Items": rows, "Latest": [i["Id"] for i in latest],
                        "LatestUnplayed": [i["Id"] for i in unplayed], "Resume": [i["Id"] for i in resume]}
    return result


def compare(root, users, state, label):
    current = snapshot(users, state["LibraryId"])
    save(root / f"{label}.json", current)
    baseline = json.loads((root / "baseline.json").read_text(encoding="utf-8"))
    for name in users:
        for section in baseline[name]:
            require(current[name][section] == baseline[name][section],
                    f"REGRESSION: {label}/{name}/{section} differs from baseline; see JSON snapshots.")
    print(f"PASS {label}: same identity, dates, watched, progress, favorites, latest and resume for BOTH users", flush=True)


def seed(root, fixture, admin):
    public = admin.call("/System/Info/Public")
    require(public["Version"] == "10.11.6" and not public["StartupWizardCompleted"],
            "Expected a NEW Jellyfin 10.11.6 fixture, not a configured server.")
    admin.call("/Startup/User")
    admin.call("/Startup/User", "POST", {"Name": "identity-admin", "Password": fixture["Password"]})
    admin.call("/Startup/Configuration", "POST", {"ServerName": "Compressor identity fixture", "UICulture": "en-US",
                                                 "MetadataCountryCode": "US", "PreferredMetadataLanguage": "en"})
    admin.call("/Startup/RemoteAccess", "POST", {"EnableRemoteAccess": False})
    admin.call("/Startup/Complete", "POST")
    admin.login("identity-admin", fixture["Password"])
    config = admin.call("/JellyfinCompressor/Configuration")
    require(not config["AutomaticCompressionEnabled"] and not config["IncludedFolders"], "Unsafe install defaults")
    admin.call("/Users/New", "POST", {"Name": "identity-second", "Password": fixture["Password"]})
    admin.call("/Library/VirtualFolders", "POST", {"LibraryOptions": {
        "EnableRealtimeMonitor": True, "EnableChapterImageExtraction": False,
        "EnableTrickplayImageExtraction": False, "EnableAutomaticSeriesGrouping": False,
        "TypeOptions": [{"Type": "Movie", "MetadataFetchers": [], "ImageFetchers": []}]}},
        name="Identity fixtures", collectionType="movies", paths=str(root / "media"), refreshLibrary="false")
    scan(admin)
    library = next(f for f in admin.call("/Library/VirtualFolders") if f["Name"] == "Identity fixtures")
    items = sorted(movies(admin), key=lambda i: i["Path"])
    require(len(items) == 3, "Synthetic movies were not indexed")
    # Give the fixtures distinct, deliberately old addition dates so ordering is meaningful.
    for index, item in enumerate(items):
        full = admin.call("/Items/" + item["Id"], userId=admin.user_id)
        full["DateCreated"] = f"2020-01-0{index + 1}T12:00:00.0000000Z"
        admin.call("/Items/" + item["Id"], "POST", full)
    state = {"ServerId": public["Id"], "LibraryId": library["ItemId"]}
    save(root / "state.json", state)
    config.update(IncludedFolders=[str(root / "media")], QuarantineDirectory=str(root / "originals"),
                  RetentionDays=30, AutomaticCompressionEnabled=False)
    config["Profiles"][0].update(VideoEncoder="libx265", Preset="ultrafast", Crf=28)
    admin.call("/JellyfinCompressor/Configuration", "POST", config)
    return state, items


def run(root, phase):
    fixture = json.loads((root / "fixture.json").read_text(encoding="utf-8"))
    admin = Api()
    if phase == "compress":
        state, items = seed(root, fixture, admin)
    else:
        state = json.loads((root / "state.json").read_text(encoding="utf-8"))
        admin.login("identity-admin", fixture["Password"])
    info = admin.call("/System/Info")
    require(info["Id"] == state["ServerId"] and info["Version"] == "10.11.6", "Wrong server")
    require(Path(info["ProgramDataPath"]).resolve() == root / "server", "Wrong server data directory")
    if phase == "shutdown":
        admin.call("/System/Shutdown", "POST")
        return
    second = Api("second")
    second.login("identity-second", fixture["Password"])
    users = {"admin": admin, "second": second}
    if phase == "compress":
        for api, played, partial, ticks in ((admin, items[1], items[2], 1200000000),
                                           (second, items[2], items[0], 1800000000)):
            api.call("/UserPlayedItems/" + played["Id"], "POST", userId=api.user_id)
            report = {"ItemId": partial["Id"], "MediaSourceId": partial["Id"], "CanSeek": True,
                      "PlayMethod": "DirectPlay", "PositionTicks": 0}
            api.call("/Sessions/Playing", "POST", report)
            report["PositionTicks"] = ticks
            api.call("/Sessions/Playing/Progress", "POST", report)
            api.call("/Sessions/Playing/Stopped", "POST", report)
            api.call("/UserFavoriteItems/" + partial["Id"], "POST", userId=api.user_id)
        scan(admin)
        baseline = snapshot(users, state["LibraryId"])
        for name, data in baseline.items():
            require(len({i["DateCreated"] for i in data["Items"]}) == 3, "Addition dates must be distinct")
            require(len(data["Latest"]) > 0 and len(data["LatestUnplayed"]) == 2 and len(data["Resume"]) == 1,
                    f"{name}: empty or incorrect test lists")
            states = [i["UserData"] for i in data["Items"]]
            require(sum(d["Played"] for d in states) == 1 and sum(d["PlaybackPositionTicks"] > 0 for d in states) == 1,
                    f"{name}: playback states were not seeded")
        save(root / "baseline.json", baseline)
        candidates = admin.call("/JellyfinCompressor/Analyze", "POST")
        require(len(candidates) == 3 and all(i["Eligible"] for i in candidates), f"Ineligible fixture: {candidates}")
        admin.call("/JellyfinCompressor/Compress", "POST")

        def completed():
            status = admin.call("/JellyfinCompressor/Status")
            save(root / "jobs.json", status)
            require(not status.get("Error"), f"Maintenance failed: {status.get('Error')}")
            jobs = status["Jobs"]
            require(all(j["Status"] not in ("Failed", "Cancelled", "Skipped", 3, 4, 5) for j in jobs),
                    "Compression failed or skipped; see jobs.json")
            originals = admin.call("/JellyfinCompressor/Originals")
            return (len(jobs) == 3 and all(j["Status"] in ("Completed", 2) for j in jobs)
                    and len(originals) == 3 and all(not r["LibraryRefreshPending"] for r in originals))

        wait_for(completed, "three real compressions and metadata refreshes", 600)
        for file in fixture["Files"]:
            path = Path(file["Path"])
            require(digest(path) != file["Sha256"] and path.stat().st_size < file["Size"], "No actual compression")
        for item in movies(admin):
            require(any(s["Type"] == "Video" and s["Codec"] == "hevc" for s in item["MediaStreams"]),
                    "Jellyfin still reports stale video metadata")
        compare(root, users, state, "after-compression")
        scan(admin)
        compare(root, users, state, "after-scan")
    elif phase == "after-restart":
        compare(root, users, state, "after-restart")
        scan(admin)
        compare(root, users, state, "after-restart-scan")
        originals = admin.call("/JellyfinCompressor/Originals")
        require(len(originals) == 3, "Expected three restorable originals")
        for record in originals:
            admin.call("/JellyfinCompressor/Originals/" + record["Request"]["Id"] + "/Restore", "POST")
        for file in fixture["Files"]:
            require(digest(Path(file["Path"])) == file["Sha256"], "Restoration changed original bytes")
        compare(root, users, state, "after-restore")
        scan(admin)
        compare(root, users, state, "after-restore-scan")
        save(root / "result.json", {"Passed": True, "Jellyfin": info["Version"],
                                    "PluginSha256": fixture["PluginSha256"], "Movies": 3, "Users": 2,
                                    "Checks": ["compression", "scan", "restart", "restart-scan", "restore", "restore-scan"]})


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("phase", choices=("prepare", "compress", "after-restart", "shutdown"))
    parser.add_argument("--fixture", type=Path, required=True)
    parser.add_argument("--ffmpeg")
    parser.add_argument("--plugin")
    args = parser.parse_args()
    fixture_root = args.fixture.resolve()
    require(fixture_root.is_relative_to(Path(__file__).resolve().parents[1] / "artifacts/identity-check"),
            "Fixture must be inside this repository's artifacts/identity-check directory")
    if args.phase == "prepare":
        require(args.ffmpeg and args.plugin, "prepare requires --ffmpeg and --plugin")
        prepare(fixture_root, args.ffmpeg, args.plugin)
    else:
        run(fixture_root, args.phase)
