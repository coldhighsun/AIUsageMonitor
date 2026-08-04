# AIUsageMonitor

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20cross--platform%20CLI-0078D6?logo=windows&logoColor=white)](#projects)
[![GitHub last commit](https://img.shields.io/github/last-commit/coldhighsun/AIUsageMonitor)](https://github.com/coldhighsun/AIUsageMonitor/commits/main)
[![GitHub stars](https://img.shields.io/github/stars/coldhighsun/AIUsageMonitor?style=flat)](https://github.com/coldhighsun/AIUsageMonitor/stargazers)
[![GitHub issues](https://img.shields.io/github/issues/coldhighsun/AIUsageMonitor)](https://github.com/coldhighsun/AIUsageMonitor/issues)
[![PRs Welcome](https://img.shields.io/badge/PRs-welcome-brightgreen.svg)](CONTRIBUTING.md)

[English](#english) | [中文](#chinese)

---

<a id="english"></a>
## English

A read-only analytics layer over Claude Code's own local usage data. It never calls the Anthropic API — it reads the files Claude Code already writes under `~/.claude` (`stats-cache.json`, `history.jsonl`, `projects/*.jsonl` session transcripts) and turns them into daily/period/model/hourly/session usage reports and cost estimates.

### Projects

- **AIUsageMonitor.Core** — class library with the data parsing and analytics engine. No dependency on Cli/WPF.
- **AIUsageMonitor.Cli** — console app (`aimon`) exposing usage reports as CLI commands.
- **AIUsageMonitor.WPF** — Windows desktop dashboard (WPF, Windows-only).

### Build

```
dotnet build AIUsageMonitor.slnx
```

### CLI usage

```
dotnet run --project src/AIUsageMonitor.Cli -- <command>
```

Commands:

- `today` — today's usage
- `week` — current week's usage
- `month` — current month's usage
- `models` — usage broken down by model
- `sessions` — per-session summaries
- `hours` — usage broken down by hour of day
- `export` — export raw analytics; supports `--format json|csv` and `--output <path>` (defaults to stdout, JSON)

### WPF dashboard (Windows only)

```
dotnet run --project src/AIUsageMonitor.WPF
```

Polls usage data once per minute and renders daily/model/hourly charts.

### How it works

Provider-specific code lives under `Providers/<Name>/` and implements `IUsageProvider`. Today there is one provider, `Providers/Claude/`: `ClaudeDataLocator` finds the Claude Code data directory, and `StatsCacheParser`, `SessionParser`, and `HistoryParser` parse its JSON/JSONL files (tolerant of malformed lines) via a source-generated `System.Text.Json` context; `ClaudeUsageProvider` wraps them behind `IUsageProvider`. `Analytics/UsageAnalyzer` computes summaries from an `IUsageProvider`'s data using `Analytics/CostCalculator` for token cost estimation. `Services/DataService` is the single facade over all of this, consumed by both the CLI and the WPF app, with a 30-second cache invalidated early by a `FileSystemWatcher` on `stats-cache.json`.

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

### 构建

```
dotnet build AIUsageMonitor.slnx
```

### CLI 用法

```
dotnet run --project src/AIUsageMonitor.Cli -- <命令>
```

可用命令:

- `today` — 今日用量
- `week` — 本周用量
- `month` — 本月用量
- `models` — 按模型统计用量
- `sessions` — 每个会话的用量汇总
- `hours` — 按小时统计用量
- `export` — 导出原始分析数据;支持 `--format json|csv` 与 `--output <path>`(默认输出到标准输出,格式为 JSON)

### WPF 仪表盘(仅 Windows)

```
dotnet run --project src/AIUsageMonitor.WPF
```

每分钟轮询一次用量数据,并渲染按天/按模型/按小时的图表。

### 工作原理

各数据源的专属代码位于 `Providers/<名称>/` 下,均实现 `IUsageProvider` 接口。目前只有一个数据源 `Providers/Claude/`:`ClaudeDataLocator` 负责定位 Claude Code 的数据目录,`StatsCacheParser`、`SessionParser`、`HistoryParser` 逐行解析其中的 JSON/JSONL 文件(容忍格式错误的行),解析过程使用源生成的 `System.Text.Json` 上下文;`ClaudeUsageProvider` 将它们封装为 `IUsageProvider`。`Analytics/UsageAnalyzer` 基于某个 `IUsageProvider` 的数据,结合 `Analytics/CostCalculator` 计算 token 成本,生成各类统计摘要。`Services/DataService` 是对上述所有逻辑的统一封装,供 CLI 与 WPF 两端共用,内部对 `stats-cache.json` 做了 30 秒缓存,并通过 `FileSystemWatcher` 提前失效。

### 许可协议

MIT — 详见 [LICENSE](LICENSE)。
