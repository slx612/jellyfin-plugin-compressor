# Queue and phase progress implementation plan

> **For agentic workers:** Use superpowers:executing-plans inline. The user approved this design and implementation with «pues dale».

**Goal:** Show the actual running phase, the pending processing order, and a separate useful history with responsive controls.
**Architecture:** Reuse JobQuery ordering and the existing embedded dashboard. Persist a nullable measured percentage and timestamps per phase on TranscodeJob; record completed phases and verified output facts. No global percentage or estimated finishing time.
**Tech Stack:** Existing .NET 9, xUnit, embedded HTML/CSS/JavaScript and node:test.
**Spec:** Approved conversation design: En curso / A continuación / Historial; per-phase progress; immediate pending/error feedback; sizes, saving, resolution, HDR checks and original retention; measure durations before optimizing checks.

## Global constraints

- Preserve originals, recovery, no-recompression, Jellyfin item identity and user data.
- Preserve user configuration; defaults remain off. Do not launch real compressions.
- Require Linux CI before publishing/installing. Work only on the Jellyfin compressor.
- Keep all existing full-file verification passes.

## Review focus

- Legacy jobs must not claim measured progress or verified output facts they did not record.
- Unknown progress must stay indeterminate; a new phase must not inherit 100% from encoding.
- Retries/restarts must not carry a stale current phase; historical transactions must match both hashes.
- Polling must preserve open details and pending actions; failures must enable retry without claiming success.
- Long queues and small screens must keep active jobs visible and retain mouse/keyboard scrolling.

### Task 1: Persist phase progress and output facts

Files: Jobs/TranscodeJob.cs, Jobs/TranscodeExecutor.cs, Jobs/JobQueue.cs, Api/PreTranscodeController.cs, JobQueueTests.cs.
- [ ] Write and run a failing regression for measured → indeterminate transitions, backwards samples and retry/restart resets.
- [ ] Implement StartPhase/ReportProgress/FinishPhase using immutable phase snapshots, timestamps and bounded percentages. Wire every executor boundary and measured callback; record verified output dimensions/format.
- [ ] Reuse JobQuery.Overview to order the existing status response without dropping active jobs.
- [ ] Run the affected .NET tests and commit.

### Task 2: Queue dashboard and action feedback

Files: Configuration/configPage.html, scripts/config-page.test.cjs.
- [ ] Write and run failing shipped-page regressions for separate active/pending/history, unknown progress, summary and pending cancellation/pause.
- [ ] Implement cards, ordered pending list, filtered/paged history and native expandable phase timings. Preserve transaction matching and restoration feedback.
- [ ] Verify shipped-page tests plus desktop/mobile browser preview, full suite and fresh review.
- [ ] Publish/install experimentally after exact-head Linux CI, preserving configuration and checking current playback/jobs before restart.

Ruling: Continue the existing in-place feature-branch workflow in the clean Jellyfin checkout. The chat's native worktree tool targets the other compressor and is unsuitable here; no worktree is created in that repository.
