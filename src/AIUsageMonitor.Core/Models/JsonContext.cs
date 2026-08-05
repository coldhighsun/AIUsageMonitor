using AIUsageMonitor.Core.Providers.Claude.Models;
using System.Text.Json.Serialization;

namespace AIUsageMonitor.Core.Models;

/// <summary>
/// Represents the JSON serialization context for the core models used in the AIUsageMonitor application. This context is used to generate source code for JSON serialization and deserialization of specific types, including <see cref="StatsCache"/>, <see cref="SessionMessage"/>, <see cref="HistoryEntry"/>, and <see cref="ExportPayload"/>.
/// </summary>
[JsonSerializable(typeof(StatsCache))]
[JsonSerializable(typeof(SessionMessage))]
[JsonSerializable(typeof(HistoryEntry))]
[JsonSerializable(typeof(ExportPayload))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
public sealed partial class CoreJsonContext : JsonSerializerContext;