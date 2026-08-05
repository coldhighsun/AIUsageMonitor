# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

- Build: `dotnet build AIUsageMonitor.slnx`
- Run CLI (binary name is `aimon`): `dotnet run --project src/AIUsageMonitor.Cli -- <command>`
  - Commands: `today`, `week`, `month`, `models`, `sessions`, `hours`, `watch`, `export`
  - `watch` takes a sub-view (`today|week|models|sessions|hours`) and refreshes it on an interval
  - `export` supports `--format json|csv` and `--output <path>` (defaults to stdout, JSON)
- Run WPF app (Windows-only): `dotnet run --project src/AIUsageMonitor.WPF`
- Run tests: `dotnet test AIUsageMonitor.slnx`
  - Single test: `dotnet test tests/AIUsageMonitor.Core.Tests --filter "FullyQualifiedName~MethodName"`
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

`DataService` caches the parsed `StatsCache` for 30 seconds and invalidates early via a `FileSystemWatcher` on `stats-cache.json` (Claude-provider-specific, via a type check in `DataService`'s constructor). Session-level summaries (`GetSessionSummaries`) are read fresh from the raw session files rather than the cache.

DI is wired through `ServiceCollectionExtensions.AddClaudeUsageCore()`, which registers the Claude provider's locator/parsers, binds it as the singleton `IUsageProvider`, and registers `CostCalculator`, `UsageAnalyzer`, and `DataService`.

**`CostCalculator`** holds a hardcoded per-model pricing table (input/output/cache-read/cache-write cost per million tokens) and resolves a model's pricing via case-insensitive substring match on the model name. When Anthropic ships a new model, add its pricing here — otherwise it silently falls through to the nearest substring match (or no match).

### Cli

`Program.cs` sets up Serilog file logging (`%LOCALAPPDATA%\aimon\logs`, daily rolling, 7-day retention), builds a generic `Host` with `AddClaudeUsageCore()`, and constructs a `System.CommandLine` root command. Each subcommand lives in `Commands/` as a static `Create(DataService)` factory and renders output through `Rendering/SpectreRenderer.cs` (Spectre.Console).

### WPF

`DashboardViewModel` polls `DataService` on a `DispatcherTimer` (once per minute) and renders daily/model/hourly series via LiveChartsCore, using CommunityToolkit.Mvvm for the MVVM plumbing.

### Releases

Pushing a `v*` tag (see `.github/workflows/ci.yml`) builds self-contained CLI binaries for win-x64/linux-x64/osx-x64/osx-arm64, attaches them to a GitHub release (marked pre-release for prerelease tags), and publishes the `aimon` dotnet tool to NuGet. The NuGet publish job runs on `ubuntu` specifically to avoid a `pwsh` glob issue.
