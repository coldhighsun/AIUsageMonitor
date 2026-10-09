# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

- Build: `dotnet build AIUsageMonitor.slnx`
- Run CLI (binary name is `aimon`): `dotnet run --project src/AIUsageMonitor.Cli -- <command>`
  - Commands: `today`, `week`, `month`, `models`, `sessions`, `projects`, `hours`, `watch`, `export`
  - `watch` takes a sub-view (`limits|today|week|models|sessions|hours`, default `limits`) and refreshes it on an interval
  - `watch` (default `--view limits`) continuously refreshes "current session" (5h) / "this week" usage with reset times (`--interval`, `--session-reset <HH:mm>`, `--week-reset <Ddd HH:mm>`; both persist to `%LOCALAPPDATA%\aimon\limits-settings.json`, and `r` re-prompts for both reset times while watching; the session reset is auto-computed at startup and never prompted for there, only the week reset is)
  - `export` supports `--format json|csv` and `--output <path>` (defaults to stdout, JSON)
  - Report commands (`today|week|month|models|hours|sessions`) take `--json` (machine-readable stdout, no progress bar, update check skipped). `today` takes `--date yyyy-MM-dd`; `week`/`month` take `--from [--to]` or `--last <days>` (`month` also `--month yyyy-MM`); `models` takes `--model <text>`. `projects` and `sessions --list` (default last 30 days, same range options) take `--top`, `--sort`, `--project`, `--model` (`Commands/ListOptions`, ordering in `Commands/UsageSorting`, JSON in `Rendering/UsageReports`; the range / load / sort / JSON-or-hint-or-table flow both commands share is `Commands/UsageList.Run`, so a new list command should reuse it); `sessions` without `--list` is the unchanged aggregate view and rejects those options. Date arguments are validated by `Commands/DateRangeResolver` (max 3650 days, years 2000-2999) and a bad one prints to stderr and exits 1; `Commands/DateRangeOptions` only defines/reads the options, and `Commands/JsonOutput` holds the shared `--json` plumbing (JSON shapes are in `Rendering/CliJsonContext`, durations as seconds).
- Run WPF app (Windows-only): `dotnet run --project src/AIUsageMonitor.WPF`
- Run tests: `dotnet test AIUsageMonitor.slnx`
  - Single test: `dotnet test tests/AIUsageMonitor.Core.Tests --filter "FullyQualifiedName~MethodName"`
  - Tests live in three projects: `tests/AIUsageMonitor.Core.Tests` (mirroring Core's `Analytics/`, `Providers/Claude/` and `Services/` folders), `tests/AIUsageMonitor.Cli.Tests` (internal CLI logic such as `LimitsAnchors`, rendering and CSV export, via `InternalsVisibleTo`) and `tests/AIUsageMonitor.WPF.Tests` (`DashboardViewModel`; Windows-only, `net10.0-windows`, which is fine because the CI test job runs on `windows-latest`).
- Versioning is via MinVer, driven by `v*` git tags (prefix `v`); no manual version bumps in project files.

## Architecture

The solution (`AIUsageMonitor.slnx`) has three projects under `src/`:

- **AIUsageMonitor.Core** — class library, `net10.0`, no dependency on Cli/WPF.
- **AIUsageMonitor.Cli** — console executable, `net10.0`, binary name `aimon`. Depends on Core.
- **AIUsageMonitor.WPF** — WPF executable, `net10.0-windows10.0.19041` (Windows-only). Depends on Core.

The tool is a **read-only analytics layer over Claude Code's own local usage data** — it never calls the Anthropic API itself. It reads the files Claude Code already writes under `~/.claude` (`stats-cache.json`, `history.jsonl`, `projects/*.jsonl` session transcripts).

### Data flow (Core)

Provider-specific code lives under `Providers/<Name>/` and implements `Providers.IUsageProvider` (`GetStatsCache()`, `GetSessionUsage()` and the hourly/recent/window queries). Today there is one provider, `Providers/Claude/`: `ClaudeDataLocator` resolves the Claude Code data directory and enumerates its files → `StatsCacheParser` / `SessionParser` / `HistoryParser` parse the JSON/JSONL line-by-line (tolerant of malformed lines) using a source-generated `System.Text.Json` context (`Models/JsonContext.cs`, AOT/trim-friendly, no reflection) → `ClaudeUsageProvider` wraps the locator/parsers behind `IUsageProvider`.

`Analytics/UsageAnalyzer` computes daily/period/model-distribution/hourly/session summaries from an `IUsageProvider`'s `StatsCache`, using `Analytics/CostCalculator` for token cost estimation → `Services/DataService` is the single facade over all of this, consumed by both Cli and WPF. `StatsCache` is currently Claude's own cache-file shape (`Providers/Claude/Models`); adding a second provider will require either normalizing its output to that shape or generalizing `UsageAnalyzer`'s input type.

`DataService` caches the parsed `StatsCache` for 10 minutes and invalidates early via `FileSystemWatcher`s on `stats-cache.json` and the session transcripts (Claude-provider-specific, via a type check in `DataService`'s constructor); an invalidation that arrives while the stats are being computed limits that result to a 2-second lifetime (`InvalidateStatsCache` bumps a version that `GetStatsCache` checks before storing), so overlapping reads still share it without it being served for long. Tokens are summed and priced (5-minute and 1-hour cache writes apart) through `Providers/Claude/ModelTokenTotals` by the builders that price usage (`RecentActivityBuilder`, `SessionBlockBuilder`, `SessionUsageBuilder`), and a response's total is `TokenUsage.TotalTokens`. Per-session and per-project usage (`GetSessionUsage` / `GetProjectUsage`, built by `Providers/Claude/SessionUsageBuilder`) is read fresh from the transcripts rather than the cache: it takes inclusive local days, aggregates by the session id on each line across files (a session is clipped to the range), counts a transcript's home lines (session id equal to the file name, which Claude Code uses to name transcripts, or no session id) before the rest so a copy in a resumed transcript is the duplicate and the session stays credited to the original's project folder, applies the model filter inside the builder, and takes the project folder and working directory from the session's earliest counted line; the project text filter is `ProjectFilter` in `DataService`. Long-running `DataService` reads accept an optional `IProgress<int>`, which the Cli surfaces as a Spectre.Console progress bar.

Transcript parsing is cached by `Providers/Claude/SessionFileCache` in three layers: an in-memory entry per file, a persistent per-file binary entry under `%LOCALAPPDATA%\aimon\cache\session-rows` (`SessionRowDiskCache`, so every new process starts warm; bump its `FormatVersion` whenever the stored row layout or the meaning of a parsed field changes), and incremental parsing - transcripts are append-only, so a changed file is extended by parsing only the bytes after `CachedSessionFile.ParsedBytes`, after a hash of the preceding bytes confirms it was not rewritten. Windowed queries (`SessionBlockBuilder`, `RecentActivityBuilder`) additionally skip files whose last-write time is older than the window via `GetFilesModifiedSince`. A disk-cache problem is always just a cache miss; the transcripts stay the source of truth.
`ClaudeDataLocator.GetSessionFiles()` caches its recursive directory scan once `DataService` calls `EnableChangeTracking()` (after its `FileSystemWatcher` is running): created/deleted/renamed events update the listing, a watcher error invalidates it, and it is rescanned every 5 minutes as a safety net; without tracking every call scans the disk. The watcher invalidates the stats cache immediately but refreshes changed transcripts through `PathChangeDebouncer` (250 ms quiet period, 1 s maximum hold-back) on a background thread, and `SessionFileCache` serializes refreshes per file so a caller and the background refresh never parse the same file twice.
`SessionFileCache.GetFilesModifiedSince` remembers each transcript's last-write time once `DataService` calls `EnableWriteTimeTracking()`: the watcher keeps it current through `NoteWritten` (a remembered time only ever moves forward), a watcher error calls `ForgetWriteTimes()`, and it is re-read from disk every minute as a safety net. Anything that makes a file newer without raising a watcher event is therefore invisible to windowed queries for up to a minute.

Resuming a Claude Code session copies the earlier transcript lines (same `uuid`) into the new file, and a streamed response is written as several lines that repeat the same usage. Every aggregation over several files (`StatsCacheBuilder`, `HourlyActivityBuilder`, `RecentActivityBuilder`, `SessionBlockBuilder`, `SessionUsageBuilder`) therefore shares one `TranscriptDeduplicator` per pass: `TryAddLine` counts a line (by `uuid`) once across files, `TryAddUsage` counts a response's tokens (by message id + request id) once. Sessions are identified per line by its own `sessionId`, and `StatsCacheBuilder` measures the longest session over each session's own lines, so copied history is credited only to the session it came from. `SessionBlockBuilder` and `RecentActivityBuilder` take an optional `TimeProvider` (clock and local time zone) so window placement, DST handling and hourly buckets are testable; production uses the system clock.

DI is wired through `ServiceCollectionExtensions.AddClaudeUsageCore()`, which registers the Claude provider's locator/parsers, binds it as the singleton `IUsageProvider`, and registers `CostCalculator`, `UsageAnalyzer`, and `DataService`.

**`CostCalculator`** holds a hardcoded per-model pricing table (input/output/cache-read/cache-write cost per million tokens) and resolves a model's pricing via case-insensitive substring match on the model name. When Anthropic ships a new model, add its pricing here — otherwise it silently falls through to the nearest substring match (or no match).

### Cli

`Program.cs` sets up Serilog file logging (`%LOCALAPPDATA%\aimon\logs`, daily rolling, 7-day retention), builds a generic `Host` with `AddClaudeUsageCore()`, and constructs a `System.CommandLine` root command. Each subcommand lives in `Commands/` as a static `Create(DataService)` factory and renders output through `Rendering/SpectreRenderer.cs` (Spectre.Console).

### WPF

`DashboardViewModel` polls `DataService` on a `DispatcherTimer` (once per minute) and renders daily/model/hourly series via LiveChartsCore, using CommunityToolkit.Mvvm for the MVVM plumbing. Each tick first rolls the date range forward when a new day has started and the range was ending on the previous day (`Core/Analytics/RollingDateRange`; a range end the user picked is left alone), and a chart whose data is empty is cleared rather than left showing the previous range. "Today" comes from an optional `TimeProvider` constructor argument (system clock by default) so this is testable (`tests/AIUsageMonitor.WPF.Tests`). The view model is a sealed `IDisposable` whose `Dispose` stops the timer; the singleton is disposed with the host on app exit.

### Releases

Pushing a `v*` tag (see `.github/workflows/ci.yml`) builds self-contained CLI binaries for win-x64/linux-x64/osx-x64/osx-arm64, attaches them to a GitHub release (marked pre-release for prerelease tags), and publishes the `aimon` dotnet tool to NuGet. The NuGet publish job runs on `ubuntu` specifically to avoid a `pwsh` glob issue.
