using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIUsageMonitor.Core.Providers.Claude.Models;

/// <summary>
/// A custom JSON converter for <see cref="DateOnly"/> that handles both "yyyy-MM-dd" format and full ISO-8601 timestamps.
/// </summary>
public sealed class DateOnlyJsonConverter : JsonConverter<DateOnly>
{
    private const string Format = "yyyy-MM-dd";

    /// <summary>
    /// Reads a <see cref="DateOnly"/> value from JSON, tolerating both "yyyy-MM-dd" format and full ISO-8601 timestamps.
    /// </summary>
    /// <param name="reader">The <see cref="Utf8JsonReader"/> to read from.</param>
    /// <param name="typeToConvert">The type to convert.</param>
    /// <param name="options">The serialization options.</param>
    /// <returns>The parsed <see cref="DateOnly"/> value.</returns>
    public override DateOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ParseDateOnly(reader.GetString()!);

    /// <summary>
    /// Writes a <see cref="DateOnly"/> value to JSON in "yyyy-MM-dd" format.
    /// </summary>
    /// <param name="writer">The <see cref="Utf8JsonWriter"/> to write to.</param>
    /// <param name="value">The <see cref="DateOnly"/> value to write.</param>
    /// <param name="options">The serialization options.</param>
    public override void Write(Utf8JsonWriter writer, DateOnly value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString(Format));

    /// <summary>
    /// Claude Code's own stats-cache.json has been observed writing this field as either a bare "yyyy-MM-dd" date or a full ISO-8601 timestamp (e.g. "2026-06-08T02:30:16.971Z"); tolerate both.
    /// </summary>
    /// <param name="value">The string representation of the date.</param>
    /// <returns>The parsed <see cref="DateOnly"/> value.</returns>
    private static DateOnly ParseDateOnly(string value) =>
        DateOnly.TryParseExact(value, Format, out var dateOnly)
            ? dateOnly
            : DateOnly.FromDateTime(DateTimeOffset.Parse(value).LocalDateTime);
}