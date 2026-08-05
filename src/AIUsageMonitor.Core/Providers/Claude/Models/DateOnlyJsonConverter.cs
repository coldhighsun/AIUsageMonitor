using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIUsageMonitor.Core.Providers.Claude.Models;

public sealed class DateOnlyJsonConverter : JsonConverter<DateOnly>
{
    private const string Format = "yyyy-MM-dd";

    public override DateOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ParseDateOnly(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, DateOnly value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString(Format));

    // Claude Code's own stats-cache.json has been observed writing this field as either a bare
    // "yyyy-MM-dd" date or a full ISO-8601 timestamp (e.g. "2026-06-08T02:30:16.971Z"); tolerate both.
    internal static DateOnly ParseDateOnly(string value) =>
        DateOnly.TryParseExact(value, Format, out var dateOnly)
            ? dateOnly
            : DateOnly.FromDateTime(DateTimeOffset.Parse(value).LocalDateTime);
}

public sealed class NullableDateOnlyJsonConverter : JsonConverter<DateOnly?>
{
    private const string Format = "yyyy-MM-dd";

    public override DateOnly? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : DateOnlyJsonConverter.ParseDateOnly(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, DateOnly? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStringValue(value.Value.ToString(Format));
        }
    }
}
