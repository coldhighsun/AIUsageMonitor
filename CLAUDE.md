# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

- Build: `dotnet build AIUsageMonitor.slnx`
- Run CLI (binary name is `aimon`): `dotnet run --project src/AIUsageMonitor.Cli -- <command>`
  - Commands: `today`, `week`, `month`, `models`, `sessions`, `hours`, `watch`, `export`
  - `watch` takes a sub-view (`limits|today|week|models|sessions|hours`, default `limits`) and refreshes it on an interval
  - `watch` (default `--view limits`) continuously refreshes "current session" (5h) / "this week" usage with reset times (`--interval`, `--session-reset <HH:mm>`, `--week-reset <Ddd HH:mm>`; both persist to `%LOCALAPPDATA%\aimon\limits-settings.json`, and `r` re-prompts for both reset times while watching; the session reset is auto-computed at startup and never prompted for there, only the week reset is)
  - `export` supports `--format json|csv` and `--output <path>` (defaults to stdout, JSON)
- Run WPF app (Windows-only): `dotnet run --project src/AIUsageMonitor.WPF`
- Run tests: `dotnet test AIUsageMonitor.slnx`
  - Single test: `dotnet test tests/AIUsageMonitor.Core.Tests --filter "FullyQualifiedName~MethodName"`
  - Tests live only under `tests/AIUsageMonitor.Core.Tests`, mirroring Core's `Analytics/` and `Providers/Claude/` folders — there are no Cli or WPF test projects.
- Versioning is via MinVer, driven by `v*` git tags (prefix `v`); no manual version bumps in project files.

## Architecture

The solution (`AIUsageMonitor.slnx`) has three projects under `src/`:

- **AIUsageMonitor.Core** — class library, `net10.0`, no dependency on Cli/WPF.
- **AIUsageMonitor.Cli** — console executable, `net10.0`, binary name `aimon`. Depends on Core.
- **AIUsageMonitor.WPF** — WPF executable, `net10.0-windows10.0.19041` (Windows-only). Depends on Core.

The tool is a **read-only analytics layer over Claude Code's own local usage data** — it never calls the Anthropic API itself. It reads the files Claude Code already writes under `~/.claude` (`stats-cache.json`, `history.jsonl`, `projects/*.jsonl` session transcripts).

### Data flow (Core)

Provider-specific code lives under `Providers/<Name>/` and implements `Providers.IUsageProvider` (`GetStatsCache()`, `GetSessionSummaries()`). Today there is one provider, `Providers/Claude/`: `ClaudeDataLocator` resolves the Claude Code data directory and enumerates its files → `StatsCacheParser` / `SessionParser` / `HistoryParser` parse the JSON/JSONL line-by-line (tolerant of malformed lines) using a source-generated `System.Text.Json` context (`Models/JsonContext.cs`, AOT/trim-friendly, no reflection) → `ClaudeUsageProvider` wraps the locator/parsers behind `IUsageProvider`.

`Analytics/UsageAnalyzer` computes daily/period/model-distribution/hourly/session summaries from an `IUsageProvider`'s `StatsCache`, using `Analytics/CostCalculator` for token cost estimation → `Services/DataService` is the single facade over all of this, consumed by both Cli and WPF. `StatsCache` is currently Claude's own cache-file shape (`Providers/Claude/Models`); adding a second provider will require either normalizing its output to that shape or generalizing `UsageAnalyzer`'s input type.

`DataService` caches the parsed `StatsCache` for 10 minutes and invalidates early via `FileSystemWatcher`s on `stats-cache.json` and the session transcripts (Claude-provider-specific, via a type check in `DataService`'s constructor); an invalidation that arrives while the stats are being computed limits that result to a 2-second lifetime (`InvalidateStatsCache` bumps a version that `GetStatsCache` checks before storing), so overlapping reads still share it without it being served for long. Session-level summaries (`GetSessionSummaries`) are read fresh from the raw session files rather than the cache. Long-running `DataService` reads accept an optional `IProgress<int>`, which the Cli surfaces as a Spectre.Console progress bar.

Transcript parsing is cached by `Providers/Claude/SessionFileCache` in three layers: an in-memory entry per file, a persistent per-file binary entry under `%LOCALAPPDATA%imonchesession-rows` (`SessionRowDiskCache`, so every new process starts warm; bump its `FormatVersion` whenever the stored row layout or the meaning of a parsed field changes), and incremental parsing - transcripts are append-only, so a changed file is extended by parsing only the bytes after `CachedSessionFile.ParsedBytes`, after a hash of the preceding bytes confirms it was not rewritten. Windowed queries (`SessionBlockBuilder`, `RecentActivityBuilder`) additionally skip files whose last-write time is older than the window via `GetFilesModifiedSince`. A disk-cache problem is always just a cache miss; the transcripts stay the source of truth.
`ClaudeDataLocator.GetSessionFiles()` caches its recursive directory scan once `DataService` calls `EnableChangeTracking()` (after its `FileSystemWatcher` is running): created/deleted/renamed events update the listing, a watcher error invalidates it, and it is rescanned every 5 minutes as a safety net; without tracking every call scans the disk. The watcher invalidates the stats cache immediately but refreshes changed transcripts through `PathChangeDebouncer` (250 ms quiet period, 1 s maximum hold-back) on a background thread, and `SessionFileCache` serializes refreshes per file so a caller and the background refresh never parse the same file twice.
`SessionFileCache.GetFilesModifiedSince` remembers each transcript's last-write time once `DataService` calls `EnableWriteTimeTracking()`: the watcher keeps it current through `NoteWritten` (a remembered time only ever moves forward), a watcher error calls `ForgetWriteTimes()`, and it is re-read from disk every minute as a safety net. Anything that makes a file newer without raising a watcher event is therefore invisible to windowed queries for up to a minute.

DI is wired through `ServiceCollectionExtensions.AddClaudeUsageCore()`, which registers the Claude provider's locator/parsers, binds it as the singleton `IUsageProvider`, and registers `CostCalculator`, `UsageAnalyzer`, and `DataService`.

**`CostCalculator`** holds a hardcoded per-model pricing table (input/output/cache-read/cache-write cost per million tokens) and resolves a model's pricing via case-insensitive substring match on the model name. When Anthropic ships a new model, add its pricing here — otherwise it silently falls through to the nearest substring match (or no match).

### Cli

`Program.cs` sets up Serilog file logging (`%LOCALAPPDATA%\aimon\logs`, daily rolling, 7-day retention), builds a generic `Host` with `AddClaudeUsageCore()`, and constructs a `System.CommandLine` root command. Each subcommand lives in `Commands/` as a static `Create(DataService)` factory and renders output through `Rendering/SpectreRenderer.cs` (Spectre.Console).

### WPF

`DashboardViewModel` polls `DataService` on a `DispatcherTimer` (once per minute) and renders daily/model/hourly series via LiveChartsCore, using CommunityToolkit.Mvvm for the MVVM plumbing.

### Releases

Pushing a `v*` tag (see `.github/workflows/ci.yml`) builds self-contained CLI binaries for win-x64/linux-x64/osx-x64/osx-arm64, attaches them to a GitHub release (marked pre-release for prerelease tags), and publishes the `aimon` dotnet tool to NuGet. The NuGet publish job runs on `ubuntu` specifically to avoid a `pwsh` glob issue.
