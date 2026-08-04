using System.Text.Json.Serialization;
using AIUsageMonitor.Core.Providers.Claude.Models;

namespace AIUsageMonitor.Core.Models;

[JsonSerializable(typeof(StatsCache))]
[JsonSerializable(typeof(SessionMessage))]
[JsonSerializable(typeof(HistoryEntry))]
[JsonSerializable(typeof(ExportPayload))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
public sealed partial class CoreJsonContext : JsonSerializerContext;