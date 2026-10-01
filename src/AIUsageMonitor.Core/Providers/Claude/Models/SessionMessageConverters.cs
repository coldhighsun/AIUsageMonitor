using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIUsageMonitor.Core.Providers.Claude.Models;

/// <summary>
/// Reads a transcript message's <c>content</c> value and reduces it to the number of <c>tool_use</c> blocks it contains,
/// without materializing the (potentially very large) content tree in memory.
/// </summary>
internal sealed class ToolUseCountConverter : JsonConverter<int>
{
    /// <summary>
    /// The UTF-8 name of the block property that identifies the block kind.
    /// </summary>
    private static ReadOnlySpan<byte> TypePropertyName => "type"u8;

    /// <summary>
    /// The UTF-8 value of the <c>type</c> property that identifies a tool call block.
    /// </summary>
    private static ReadOnlySpan<byte> ToolUseValue => "tool_use"u8;

    /// <summary>
    /// Counts the <c>tool_use</c> blocks in a content array; any other content shape (e.g. a plain string) yields zero.
    /// </summary>
    /// <param name="reader">The reader positioned on the <c>content</c> value.</param>
    /// <param name="typeToConvert">The target type (always <see cref="int"/>).</param>
    /// <param name="options">The serializer options in effect.</param>
    /// <returns>The number of <c>tool_use</c> blocks found.</returns>
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();

            return 0;
        }

        var count = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();

                continue;
            }

            var isToolUse = false;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var isTypeProperty = reader.ValueTextEquals(TypePropertyName);
                reader.Read();

                if (isTypeProperty)
                {
                    isToolUse = reader.TokenType == JsonTokenType.String && reader.ValueTextEquals(ToolUseValue);
                }

                reader.Skip();
            }

            if (isToolUse)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Not supported; the tool-use count is a read-only projection of the transcript content.
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <param name="value">The value.</param>
    /// <param name="options">The serializer options in effect.</param>
    /// <exception cref="NotSupportedException">Always thrown.</exception>
    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Reads a transcript timestamp string into a <see cref="DateTimeOffset"/> once at parse time, yielding
/// <see langword="null"/> (instead of failing the whole line) when the value is missing or malformed.
/// </summary>
internal sealed class LenientTimestampConverter : JsonConverter<DateTimeOffset?>
{
    /// <summary>
    /// Parses the timestamp token, tolerating non-string and unparsable values.
    /// </summary>
    /// <param name="reader">The reader positioned on the timestamp value.</param>
    /// <param name="typeToConvert">The target type.</param>
    /// <param name="options">The serializer options in effect.</param>
    /// <returns>The parsed timestamp, or <see langword="null"/> when it cannot be parsed.</returns>
    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            reader.Skip();

            return null;
        }

        if (reader.TryGetDateTimeOffset(out var iso))
        {
            return iso;
        }

        return DateTimeOffset.TryParse(reader.GetString(), CultureInfo.CurrentCulture, out var parsed) ? parsed : null;
    }

    /// <summary>
    /// Not supported; transcript rows are only ever deserialized.
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <param name="value">The value.</param>
    /// <param name="options">The serializer options in effect.</param>
    /// <exception cref="NotSupportedException">Always thrown.</exception>
    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        throw new NotSupportedException();
    }
}
