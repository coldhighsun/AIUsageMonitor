# AIUsageMonitor

[![CI](https://github.com/coldhighsun/AIUsageMonitor/actions/workflows/ci.yml/badge.svg)](https://github.com/coldhighsun/AIUsageMonitor/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![GitHub release](https://img.shields.io/github/v/release/coldhighsun/AIUsageMonitor?logo=github)](https://github.com/coldhighsun/AIUsageMonitor/releases/latest)
[![GitHub Release Downloads](https://img.shields.io/github/downloads/coldhighsun/AIUsageMonitor/total?logo=github&label=release%20downloads)](https://github.com/coldhighsun/AIUsageMonitor/releases)
[![NuGet Tool Downloads](https://img.shields.io/nuget/dt/AIUsageMonitor.Cli?logo=nuget&label=nuget%20downloads)](https://www.nuget.org/packages/AIUsageMonitor.Cli)
[![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20cross--platform%20CLI-0078D6?logo=windows&logoColor=white)](#projects)
[![GitHub last commit](https://img.shields.io/github/last-commit/coldhighsun/AIUsageMonitor)](https://github.com/coldhighsun/AIUsageMonitor/commits/main)

[English](#english) | [中文](#chinese)

---

<a id="english"></a>
## English

A read-only analytics layer over Claude Code's own local usage data. It never calls the Anthropic API — it reads the files Claude Code already writes under `~/.claude` (`stats-cache.json`, `history.jsonl`, `projects/*.jsonl` session transcripts) and turns them into daily/period/model/hourly/session usage reports and cost estimates.

### Projects

- **AIUsageMonitor.Core** — class library with the data parsing and analytics engine. No dependency on Cli/WPF.
- **AIUsageMonitor.Cli** — console app (`aimon`) exposing usage reports as CLI commands.
- **AIUsageMonitor.WPF** — Windows desktop dashboard (WPF, Windows-only).

### Install

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

### CLI usage

```
aimon <command>
```

(or, from source: `dotnet run --project src/AIUsageMonitor.Cli -- <command>`)

Commands:

- `today` — today's usage
- `week` — current week's usage
- `month` — current month's usage
- `models` — usage broken down by model
- `sessions` — per-session summaries
- `hours` — usage broken down by hour of day
- `watch` — live-updating view (`limits|today|week|models|sessions|hours`, default `limits`), refreshed on an interval
- `export` — export raw analytics; supports `--format json|csv` and `--output <path>` (defaults to stdout, JSON)

`watch`'s default view, `limits`, approximates the "Current Session" (rolling 5-hour window) and "This Week" usage panels shown in Claude's own account UI, including a **Time Progress** bar (how far each window has elapsed) and a **Token Progress** bar (how close the window is to its token budget). Since the real reset times live on the Anthropic account and can't be read locally, they're estimated from local transcript timestamps unless pinned with `--session-reset "HH:mm"` (e.g. `--session-reset "18:30"` — just copy the reset time Claude itself shows) / `--week-reset "Ddd HH:mm"` (e.g. `--week-reset "Mon 09:00"`). When the session reset time has elapsed and this machine has no activity in the last 5 hours (the account may be idle, or in use on another device), the session row shows as unknown rather than a made-up countdown. The weekly reset is a fixed time assigned to your account, unrelated to activity, so it can't be derived locally at all — without `--week-reset` that row also shows as unknown, with the trailing 7 days of usage shown as an upper bound on the current cycle instead; that fixed time only needs to be entered once, unlike the session reset.

Claude itself only ever shows a usage *percentage*, never the underlying token limit, so the Token Progress bar works the same way: pass `--session-token-progress <percent>` / `--week-token-progress <percent>` (e.g. `--session-token-progress 32` for "32%", copied from Settings > Usage or `/usage` in Claude Code) and `watch` derives a token budget from that percentage and the tokens it has counted for the window so far, then tracks the bar live against that budget without asking again.

Press `r` while `watch` is running to re-enter any of the four values — reset times and usage percentages (Enter on a prompt keeps its current value or local estimate). If any of the four aren't configured and you're in an interactive terminal, `watch` prompts for them once up front and remembers the answers in `%LOCALAPPDATA%/aimon/limits-settings.json` (or the OS equivalent) for future runs.

![watch --view limits](docs/images/watch-limits-screenshot.png)

### WPF dashboard (Windows only)

> **⚠️ Work in progress — not ready for use yet.** The WPF project is still under active development; expect missing features and rough edges. Use the CLI (`aimon`) for now.

```
dotnet run --project src/AIUsageMonitor.WPF
```

Polls usage data once per minute and renders daily/model/hourly charts.

### How it works

Provider-specific code lives under `Providers/<Name>/` and implements `IUsageProvider`. Today there is one provider, `Providers/Claude/`: `ClaudeDataLocator` finds the Claude Code data directory, and `StatsCacheParser`, `SessionParser`, and `HistoryParser` parse its JSON/JSONL files (tolerant of malformed lines) via a source-generated `System.Text.Json` context; `ClaudeUsageProvider` wraps them behind `IUsageProvider`. `Analytics/UsageAnalyzer` computes summaries from an `IUsageProvider`'s data using `Analytics/CostCalculator` for token cost estimation. `Services/DataService` is the single facade over all of this, consumed by both the CLI and the WPF app, with a 30-second cache invalidated early by a `FileSystemWatcher` on `stats-cache.json`. Long-running reads accept an optional `IProgress<int>`, which the CLI surfaces as a Spectre.Console progress bar.

### Releases

Pushing a `v*` tag builds self-contained CLI binaries for win-x64/linux-x64/osx-x64/osx-arm64, attaches them to a GitHub release, and publishes the `aimon` dotnet tool to NuGet (see `.github/workflows/ci.yml`).

### License

MIT — see [LICENSE](LICENSE).

---

<a id="chinese"></a>
## 中文

一个基于 Claude Code 本地使用数据的**只读**分析工具。它从不调用 Anthropic API，只读取 Claude Code 自身已经写入 `~/.claude` 目录下的文件(`stats-cache.json`、`history.jsonl`、`projects/*.jsonl` 会话记录),并将其转换为按天/按周期/按模型/按小时/按会话的用量报告与成本估算。

### 项目结构

- **AIUsageMonitor.Core** — 数据解析与分析引擎所在的类库,不依赖 Cli/WPF。
- **AIUsageMonitor.Cli** — 控制台程序(命令名 `aimon`),以命令行方式输出用量报告。
- **AIUsageMonitor.WPF** — Windows 桌面仪表盘(WPF,仅支持 Windows)。

### 安装

通过 [winget](https://learn.microsoft.com/en-us/windows/package-manager/winget/) 安装:

```
winget install coldhighsun.AIUsageMonitor.Cli
```

或通过 [dotnet tool](https://learn.microsoft.com/en-us/dotnet/core/tools/global-tools-how-to-use) 安装(跨平台):

```
dotnet tool install --global AIUsageMonitor.Cli
```

或从源码构建:

```
dotnet build AIUsageMonitor.slnx
```

### CLI 用法

```
aimon <命令>
```

(或从源码运行:`dotnet run --project src/AIUsageMonitor.Cli -- <命令>`)

可用命令:

- `today` — 今日用量
- `week` — 本周用量
- `month` — 本月用量
- `models` — 按模型统计用量
- `sessions` — 每个会话的用量汇总
- `hours` — 按小时统计用量
- `watch` — 实时刷新视图(`limits|today|week|models|sessions|hours`,默认为 `limits`),按指定间隔自动刷新
- `export` — 导出原始分析数据;支持 `--format json|csv` 与 `--output <path>`(默认输出到标准输出,格式为 JSON)

`watch` 的默认视图 `limits` 近似展示 Claude 官方账户界面中的 "Current Session"(滚动 5 小时窗口)和 "This Week" 用量面板,并附带 **Time Progress**(当前窗口已经过去的时间比例)与 **Token Progress**(当前窗口用量占预算的比例)两条进度条。由于真实的重置时间存储在 Anthropic 账号侧,本地无法读取,默认会根据本地会话记录的时间戳估算;也可以用 `--session-reset "HH:mm"`(如 `--session-reset "18:30"`,照抄 Claude 显示的重置时间即可)/ `--week-reset "Ddd HH:mm"`(如 `--week-reset "Mon 09:00"`)锚定从 Claude 官方界面查到的真实值。会话重置时间过期、且本机近 5 小时无活动记录时(可能账户空闲,也可能正在其他设备使用),会话行会显示为"未知"而不是编造的倒计时。周重置是账号固定的每周时刻,与活动无关,本地无法推算,所以未配置 `--week-reset` 时周行同样显示为"未知",只按近 7 天用量给出当前周期用量的上界;这个固定时刻只需录入一次,不会像会话那样过期。

Claude 官方界面本身也只显示用量**百分比**,从不显示背后的 token 上限,因此 Token Progress 走同样的逻辑:传入 `--session-token-progress <百分比>` / `--week-token-progress <百分比>`(如 `--session-token-progress 32`,照抄 Settings > Usage 或 Claude Code 里 `/usage` 显示的 "32%"),`watch` 会结合这个百分比与当前已统计到的 token 数反推出一个预算,之后就能持续实时对照这个预算刷新进度条,不用每次都重新问。

在 `watch` 运行中按 `r` 可随时重新录入这四个值——两个重置时刻和两个用量百分比(提示时直接回车会保留当前值或本地估算)。如果这四项中有任意一项未配置且在交互式终端中运行,`watch` 会一次性提示输入,并把结果保存到 `%LOCALAPPDATA%/aimon/limits-settings.json`(或对应系统的等效路径)供后续运行复用。

![watch --view limits](docs/images/watch-limits-screenshot.png)

### WPF 仪表盘(仅 Windows)

> **⚠️ 尚在开发中,暂不建议使用。** WPF 项目目前仍在积极开发,功能不完整,可能存在明显问题。请先使用 CLI(`aimon`)。

```
dotnet run --project src/AIUsageMonitor.WPF
```

每分钟轮询一次用量数据,并渲染按天/按模型/按小时的图表。

### 工作原理

各数据源的专属代码位于 `Providers/<名称>/` 下,均实现 `IUsageProvider` 接口。目前只有一个数据源 `Providers/Claude/`:`ClaudeDataLocator` 负责定位 Claude Code 的数据目录,`StatsCacheParser`、`SessionParser`、`HistoryParser` 逐行解析其中的 JSON/JSONL 文件(容忍格式错误的行),解析过程使用源生成的 `System.Text.Json` 上下文;`ClaudeUsageProvider` 将它们封装为 `IUsageProvider`。`Analytics/UsageAnalyzer` 基于某个 `IUsageProvider` 的数据,结合 `Analytics/CostCalculator` 计算 token 成本,生成各类统计摘要。`Services/DataService` 是对上述所有逻辑的统一封装,供 CLI 与 WPF 两端共用,内部对 `stats-cache.json` 做了 30 秒缓存,并通过 `FileSystemWatcher` 提前失效。耗时较长的读取操作支持可选的 `IProgress<int>` 参数,CLI 端会将其渲染为 Spectre.Console 进度条。

### 发布

推送 `v*` 标签会为 win-x64/linux-x64/osx-x64/osx-arm64 构建自包含的 CLI 二进制文件,附加到 GitHub Release,并将 `aimon` dotnet 工具发布到 NuGet(详见 `.github/workflows/ci.yml`)。

### 许可协议

MIT — 详见 [LICENSE](LICENSE)。
