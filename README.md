# AIUsageMonitor

[![CI](https://github.com/coldhighsun/AIUsageMonitor/actions/workflows/ci.yml/badge.svg)](https://github.com/coldhighsun/AIUsageMonitor/actions/workflows/ci.yml)
[![codecov](https://codecov.io/gh/coldhighsun/AIUsageMonitor/branch/main/graph/badge.svg)](https://codecov.io/gh/coldhighsun/AIUsageMonitor)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20cross--platform%20CLI-0078D6?logo=windows&logoColor=white)](#projects)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![GitHub release](https://img.shields.io/github/v/release/coldhighsun/AIUsageMonitor?logo=github)](https://github.com/coldhighsun/AIUsageMonitor/releases/latest)
[![GitHub Release Downloads](https://img.shields.io/github/downloads/coldhighsun/AIUsageMonitor/total?logo=github&label=release%20downloads)](https://github.com/coldhighsun/AIUsageMonitor/releases)
[![NuGet](https://img.shields.io/nuget/v/AIUsageMonitor.Cli?logo=nuget&label=nuget)](https://www.nuget.org/packages/AIUsageMonitor.Cli)
[![NuGet Prerelease](https://img.shields.io/nuget/vpre/AIUsageMonitor.Cli?logo=nuget&label=nuget%20pre-release&color=orange)](https://www.nuget.org/packages/AIUsageMonitor.Cli/absoluteLatest)
[![NuGet Tool Downloads](https://img.shields.io/nuget/dt/AIUsageMonitor.Cli?logo=nuget&label=nuget%20downloads)](https://www.nuget.org/packages/AIUsageMonitor.Cli)
[![GitHub last commit](https://img.shields.io/github/last-commit/coldhighsun/AIUsageMonitor)](https://github.com/coldhighsun/AIUsageMonitor/commits/main)

English | [中文](README.zh-CN.md)

---

A read-only analytics layer over Claude Code's own local usage data. It never calls the Anthropic API — it reads the files Claude Code already writes under `~/.claude` (`stats-cache.json`, `history.jsonl`, `projects/*.jsonl` session transcripts) and turns them into daily/period/model/hourly/session usage reports and cost estimates.

## Projects

- **AIUsageMonitor.Core** — class library with the data parsing and analytics engine. No dependency on Cli/WPF.
- **AIUsageMonitor.Cli** — console app (`aimon`) exposing usage reports as CLI commands.
- **AIUsageMonitor.WPF** — Windows desktop dashboard (WPF, Windows-only).

## Install

via [winget](https://learn.microsoft.com/en-us/windows/package-manager/winget/)

```
winget install coldhighsun.AIUsageMonitor.Cli
```

Or via [dotnet tool](https://learn.microsoft.com/en-us/dotnet/core/tools/global-tools-how-to-use) (cross-platform):

```
dotnet tool install --global AIUsageMonitor.Cli
```

Or build from source:

```
dotnet build AIUsageMonitor.slnx
```

## CLI usage

```
aimon <command>
```

(or, from source: `dotnet run --project src/AIUsageMonitor.Cli -- <command>`)

> 💡 **Start with `aimon watch`** — it's the recommended way to use this tool day-to-day: a live-updating view of your current session and weekly limits, so you always know how much headroom you have left.

Commands:

- `watch` — **(recommended)** live-updating view (`limits|today|week|models|sessions|hours`, default `limits`), refreshed on an interval
- `today` — today's usage
- `week` — current week's usage
- `month` — current month's usage
- `models` — usage broken down by model
- `sessions` — overall session statistics; with `--list`, the individual sessions
- `projects` — usage per project
- `hours` — usage broken down by hour of day
- `export` — export raw analytics; supports `--format json|csv` and `--output <path>` (defaults to stdout, JSON)

Report options (`today`, `week`, `month`, `models`, `hours`, `sessions`):

- `--json` — print the report as JSON (no progress bar or table; the update check is skipped) for scripts
- `today --date yyyy-MM-dd` — show another day
- `week` / `month`: `--from yyyy-MM-dd [--to yyyy-MM-dd]` or `--last <days>`; `month` also takes `--month yyyy-MM`. Ranges are limited to 3650 days and years 2000-2999; invalid values print an error to stderr and exit with code 1
- `models --model <text>` — only models whose name contains the text (percentages stay relative to all models). If nothing matches, the table view prints `No model matches '<text>'.` to stderr and exits 0, and `--json` prints `[]`

Per-project and per-session usage (`projects`, `sessions --list`):

- Range: `--from`/`--to`, `--last`, `--month` as above; the default is the **last 30 days**
- `--top <n>` — only the first N rows; `--sort` — `projects`: `tokens|cost|sessions|messages`, `sessions --list`: `tokens|cost|messages|duration|start` (largest or newest first, default `tokens`); a Messages column is added to the table when the rows are ordered by `messages`
- `--project <text>` — only projects whose working directory or `projects/` folder name contains the text; `--model <text>` — only count usage of matching models (for sessions, only those assistant messages are counted)
- `--json` prints `totalProjects` / `totalSessions` (matches before `--top`) and the rows, with durations as `durationSeconds`
- `sessions` without `--list` is unchanged; giving it a range, `--top`, `--sort`, `--project` or `--model` without `--list` is an error
- A session is clipped to the range (it shows only what happened inside it) and may span several transcript files; `messages` counts transcript lines and `duration` is the wall-clock time between the first and last message, idle gaps included. Tokens match `month` for the same days; the cost here prices every message exactly (including 5-minute and 1-hour cache writes), so it can differ a little from `month`, which scales an all-time model mix, and `month` counts a session once per day it was active

`watch`'s default view, `limits`, approximates the "Current Session" (rolling 5-hour window) and "This Week" panels from Claude's own account UI, each with a **Time Progress** bar (how far the window has elapsed, colored green/orange when token usage is pacing notably behind/ahead of it) and a **Token Progress** bar (how close it is to its budget). For the session window, a pace hint also appears below the table when token usage is running noticeably ahead of or behind elapsed time, suggesting you slow down or use more freely.

The real reset times and token limits live on your Anthropic account, not locally, so pin them once:

- `--session-reset "HH:mm"` — the session reset time Claude shows (e.g. `"18:30"`)
- `--week-reset "Ddd HH:mm"` — the weekly reset (e.g. `"Mon 09:00"`); fixed per account, so it only needs entering once, unlike the session reset
- `--session-token-progress <percent>` / `--week-token-progress <percent>` — the usage % Claude shows (e.g. `32`), used to back out a usage budget. Each percentage is saved together with the cost counted at that moment, and the budget is derived from it; a percentage given while nothing is counted for the window yet is ignored (the saved one is kept)
- `--cache-read-weight <0-1>` — what share of the list price of cache-read tokens counts toward the usage bar (default `0.1`, a heuristic: the subscription quota is not metered at API prices, and cache reads dominate long sessions). Lower it if the bar runs ahead of Claude's own percentage, raise it if it runs behind; the saved percentages are kept and the budgets re-derived, so there is no need to read `/usage` again. It is saved when given; `--reset-cache-read-weight` forgets it. A line below the table shows what the bar is made of (cache read / output / cache write / input)

Without these, values are estimated from local transcript timestamps, and a row shows as **unknown** rather than a guessed countdown when there isn't enough local evidence (no activity in the last 5 hours, or no weekly anchor at all).

Press `r` while `watch` is running to re-enter any of the four (Enter keeps the current value). In an interactive terminal, unset values are prompted for once up front and saved to `%LOCALAPPDATA%/aimon/limits-settings.json` (or the OS equivalent) for future runs.

![watch --view limits](docs/images/watch-limits-screenshot.png)

## Update checks

Every command checks GitHub for a newer release once after it finishes (`watch` checks once before entering its refresh loop and keeps the notice pinned to the bottom of the view for the whole session). Runs with `--json` skip the check entirely, so scripted calls make no network request and never wait for one; the notice itself is written to stderr. If a newer version is available, a one-line notice with the new version and a link to the release is printed — this never blocks or fails the command, and no data is sent beyond the standard GitHub API request for the latest release.

## WPF dashboard (Windows only)

> **⚠️ Work in progress — not ready for use yet.** The WPF project is still under active development; expect missing features and rough edges. Use the CLI (`aimon`) for now.

```
dotnet run --project src/AIUsageMonitor.WPF
```

Polls usage data once per minute and renders daily/model/hourly charts.

## Limitations

⚠️ **Multi-device usage**: If you use Claude Code on multiple devices within a single billing period (session window or weekly cycle), the reported statistics and progress bars may be inaccurate. Since this tool reads only local data files (`stats-cache.json`, `history.jsonl`, and session transcripts from `~/.claude`), it cannot see usage from other devices. To ensure accurate tracking across devices, manually configure `--session-token-progress` and `--week-token-progress` with the percentages shown on your Anthropic account's official UI, so the tool can infer the true budget limits.

## How it works

Provider-specific parsing (`Providers/<Name>/`, e.g. `Providers/Claude/`) feeds `Analytics/UsageAnalyzer`, exposed through `Services/DataService` to both the CLI and the WPF app. See [CLAUDE.md](CLAUDE.md) for the full data-flow breakdown.

## Releases

Pushing a `v*` tag builds self-contained CLI binaries for win-x64/linux-x64/osx-x64/osx-arm64, attaches them to a GitHub release, and publishes the `aimon` dotnet tool to NuGet (see `.github/workflows/ci.yml`).

## License

MIT — see [LICENSE](LICENSE).
