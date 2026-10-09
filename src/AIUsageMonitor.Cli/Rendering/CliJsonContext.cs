using AIUsageMonitor.Core.Models;
using System.Text.Json.Serialization;

namespace AIUsageMonitor.Cli.Rendering;

/// <summary>
/// The source-generated JSON serialization context for the reports the CLI prints with <c>--json</c>.
/// </summary>
[JsonSerializable(typeof(DailySummary))]
[JsonSerializable(typeof(PeriodSummary))]
[JsonSerializable(typeof(List<ModelDistribution>))]
[JsonSerializable(typeof(List<HourlyActivity>))]
[JsonSerializable(typeof(SessionStatsReport))]
[JsonSerializable(typeof(ProjectListReport))]
[JsonSerializable(typeof(SessionListReport))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
internal sealed partial class CliJsonContext : JsonSerializerContext;
