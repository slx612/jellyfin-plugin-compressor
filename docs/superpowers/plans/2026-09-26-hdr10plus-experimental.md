# Experimental HDR10+ Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Allow explicitly selected HDR10+ MKV movies to be compressed while preserving HDR10+, Dolby Vision 8.1 when present, tracks, chapters and Jellyfin identity.

**Architecture:** Keep the existing manual HDR pipeline. Encode with Jellyfin FFmpeg into a private intermediate MKV, extract source HDR10+ metadata, inject it into the new HEVC video, and remux that video with the encoded MKV's other tracks. Compare source/output metadata, stream timing and structure before the existing replacement transaction may run. Tools ship in the plugin ZIP for Linux x86-64 Docker/Synology; unavailable platforms fail closed.

**Tech Stack:** Jellyfin 10.11.6, .NET 9, Jellyfin FFmpeg, hdr10plus_tool 1.7.2, MKVToolNix 102.0.

**Spec:** `docs/hdr-experimental.md` and the approved manual, opt-in design in the task conversation.

## Global Constraints

- Automatic and batch compression must keep skipping all HDR content.
- HDR10+ is off by default and only for manual single-film MKV jobs with libx265, unchanged frame dimensions and frame count.
- Any missing tool, failed process, metadata mismatch, track/timing mismatch, or changed configuration leaves the original untouched.
- No installation or release before a complete-film run in the user's Linux server and playback validation.

## Review Focus

- Source HDR10+ detected only on decoded frames must follow the HDR10+ route.
- Metadata extracted from the output must match the source exactly, not merely have the same frame count.
- Audio, subtitles, chapters, marker, languages and timestamps must survive remuxing.
- Restart, cancellation and out-of-space errors must leave the original and queue recoverable.
- Bundled executables must be pinned, verified and usable without FUSE in Docker.

---

### Task 1: Eligibility and verification policy

**Files:** `PluginConfiguration.cs`, `CompressionPolicy.cs`, `DynamicHdrDetector.cs`, `configPage.html`, `CompressorHdrTests.cs`.

- [ ] Write failing tests for disabled-by-default/manual-only HDR10+ and metadata-frame preservation.
- [ ] Make the policy accept only opted-in, single-film, HEVC/MKV HDR10+ with unchanged dimensions.
- [ ] Expose the switch and clear eligibility/error text in the dashboard.
- [ ] Run the targeted and full test suites, then commit.

### Task 2: Toolchain and safe output verification

**Files:** create `Media/Hdr10PlusToolchain.cs`; modify `TranscodeExecutor.cs`; add focused tests.

- [ ] Write failing tests for missing tools, exact metadata mismatch and non-video stream mismatch.
- [ ] Implement bounded, cancellable process execution with argument lists and private temporary files.
- [ ] Extract source HDR10+, demux the encoded MKV, inject metadata, remux with original copied tracks and timestamp file.
- [ ] Verify extracted HDR10+ JSON hash, frame facts, non-video framecrc, chapters, marker and existing probe checks before publish.
- [ ] Run the targeted and full test suites, then commit.

### Task 3: Bundle Linux x86-64 tools

**Files:** `build-plugin.ps1`, `.github/workflows/build.yml`, `README.md`, third-party notice.

- [ ] Pin upstream archives and SHA-256 values; package only the Linux x86-64 runtime tools required for this experimental path.
- [ ] Make the MKVToolNix AppImage usable in Docker without FUSE and document platform availability.
- [ ] Build a ZIP and verify its contents; run CI and commit.

### Task 4: Real validation and draft PR

- [ ] Re-run the short-clip comparison with the plugin's own orchestration and bundled tools.
- [ ] Test a complete film to a temporary location on the server, without replacing the original, and inspect playback in a Dolby Vision client.
- [ ] Keep the PR draft until every check passes and user-facing documentation reflects actual limits.
