using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIUsageMonitor.Core.Providers.Claude.Models;

/// <summary>
/// Reads a transcript message's <c>content</c> value and reduces it to a <see cref="ContentSummary"/> (block counts,
/// tool names and tool-result errors), without materializing the (potentially very large) content tree in memory and
/// without retaining any text, tool input or tool output.
/// </summary>
internal sealed class ContentSummaryConverter : JsonConverter<ContentSummary>
{
    /// <summary>
    /// Gets a value indicating that a JSON <c>null</c> is also handled by the converter, so it yields the empty
    /// summary instead of leaving the property null.
    /// </summary>
    public override bool HandleNull => true;

    /// <summary>
    /// The kinds of content block that are told apart.
    /// </summary>
    private enum BlockKind
    {
        /// <summary>
        /// Any other kind of block.
        /// </summary>
        Other,

        /// <summary>
        /// A tool call.
        /// </summary>
        ToolUse,

        /// <summary>
        /// A tool result.
        /// </summary>
        ToolResult,

        /// <summary>
        /// A thinking block.
        /// </summary>
        Thinking,

        /// <summary>
        /// A text block.
        /// </summary>
        Text,
    }

    /// <summary>
    /// Summarizes a content array; any other content shape (e.g. a plain string) yields an empty summary.
    /// </summary>
    /// <param name="reader">The reader positioned on the <c>content</c> value.</param>
    /// <param name="typeToConvert">The target type.</param>
    /// <param name="options">The serializer options in effect.</param>
    /// <returns>The summary of the content blocks.</returns>
    public override ContentSummary Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();

            return ContentSummary.Empty;
        }

        var toolUse = 0;
        var toolResults = 0;
        var toolResultErrors = 0;
        var thinking = 0;
        var text = 0;
        List<string>? toolNames = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();

                continue;
            }

            var blockType = BlockKind.Other;
            string? name = null;
            var isError = false;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                // Property names are compared as UTF-8 bytes, so only the few values that are kept allocate a string.
                var isType = reader.ValueTextEquals("type"u8);
                var isName = !isType && reader.ValueTextEquals("name"u8);
                var isErrorFlag = !isType && !isName && reader.ValueTextEquals("is_error"u8);
                reader.Read();

                if (isType && reader.TokenType == JsonTokenType.String)
                {
                    blockType = reader.ValueTextEquals("tool_use"u8) ? BlockKind.ToolUse
                        : reader.ValueTextEquals("tool_result"u8) ? BlockKind.ToolResult
                        : reader.ValueTextEquals("thinking"u8) || reader.ValueTextEquals("redacted_thinking"u8) ? BlockKind.Thinking
                        : reader.ValueTextEquals("text"u8) ? BlockKind.Text
                        : BlockKind.Other;
                }
                else if (isName && reader.TokenType == JsonTokenType.String)
                {
                    name = reader.GetString();
                }
                else if (isErrorFlag && reader.TokenType == JsonTokenType.True)
                {
                    isError = true;
                }
                else
                {
                    reader.Skip();
                }
            }

            switch (blockType)
            {
                case BlockKind.ToolUse:
                    toolUse++;
                    (toolNames ??= []).Add(name ?? "");
                    break;
                case BlockKind.ToolResult:
                    toolResults++;
                    if (isError)
                    {
                        toolResultErrors++;
                    }

                    break;
                case BlockKind.Thinking:
                    thinking++;
                    break;
                case BlockKind.Text:
                    text++;
                    break;
            }
        }

        return toolUse + toolResults + thinking + text == 0
            ? ContentSummary.Empty
            : new()
            {
                ToolUseCount = toolUse,
                ToolNames = toolNames ?? [],
                ToolResultCount = toolResults,
                ToolResultErrorCount = toolResultErrors,
                ThinkingBlockCount = thinking,
                TextBlockCount = text,
            };
    }

    /// <summary>
    /// Not supported; the summary is a read-only projection of the transcript content.
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <param name="value">The value.</param>
    /// <param name="options">The serializer options in effect.</param>
    /// <exception cref="NotSupportedException">Always thrown.</exception>
    public override void Write(Utf8JsonWriter writer, ContentSummary value, JsonSerializerOptions options)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Reads a nested object, yielding <see langword="null"/> (instead of failing the whole line and the usage on it) when the
/// value is not a JSON object.
/// </summary>
/// <typeparam name="T">The object type.</typeparam>
internal sealed class LenientObjectConverter<T> : JsonConverter<T?> where T : class
{
    /// <summary>
    /// Reads the object, skipping anything that is not an object.
    /// </summary>
    /// <param name="reader">The reader positioned on the value.</param>
    /// <param name="typeToConvert">The target type.</param>
    /// <param name="options">The serializer options in effect.</param>
    /// <returns>The object, or <see langword="null"/>.</returns>
    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();

            return null;
        }

        return JsonSerializer.Deserialize(ref reader, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)options.GetTypeInfo(typeof(T)));
    }

    /// <summary>
    /// Not supported; transcript rows are only ever deserialized.
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <param name="value">The value.</param>
    /// <param name="options">The serializer options in effect.</param>
    /// <exception cref="NotSupportedException">Always thrown.</exception>
    public override void Write(Utf8JsonWriter writer, T? value, JsonSerializerOptions options)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Reads a whole number that is not optional, yielding zero (instead of failing the whole line) when the value is not a
/// number or a numeric string.
/// </summary>
internal sealed class LenientInt64Converter : JsonConverter<long>
{
    /// <summary>
    /// Reads the number.
    /// </summary>
    /// <param name="reader">The reader positioned on the value.</param>
    /// <param name="typeToConvert">The target type.</param>
    /// <param name="options">The serializer options in effect.</param>
    /// <returns>The number, or zero.</returns>
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var result = 0L;
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                if (reader.TryGetInt64(out var integer))
                {
                    result = integer;
                }
                else if (reader.TryGetDouble(out var real) && real is >= -9.2233720368547758E+18 and < 9.2233720368547758E+18)
                {
                    result = (long)real;
                }

                break;
            case JsonTokenType.String when long.TryParse(reader.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                result = parsed;
                break;
        }

        reader.Skip();

        return result;
    }

    /// <summary>
    /// Not supported; transcript rows are only ever deserialized.
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <param name="value">The value.</param>
    /// <param name="options">The serializer options in effect.</param>
    /// <exception cref="NotSupportedException">Always thrown.</exception>
    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Reads a floating-point number that is not optional, yielding zero (instead of failing the whole line) for any other value.
/// </summary>
internal sealed class LenientRequiredDoubleConverter : JsonConverter<double>
{
    /// <summary>
    /// Reads the number.
    /// </summary>
    /// <param name="reader">The reader positioned on the value.</param>
    /// <param name="typeToConvert">The target type.</param>
    /// <param name="options">The serializer options in effect.</param>
    /// <returns>The number, or zero.</returns>
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var result = reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out var value) ? value : 0d;
        reader.Skip();

        return result;
    }

    /// <summary>
    /// Not supported; transcript rows are only ever deserialized.
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <param name="value">The value.</param>
    /// <param name="options">The serializer options in effect.</param>
    /// <exception cref="NotSupportedException">Always thrown.</exception>
    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Reads a flag, yielding <see langword="false"/> (instead of failing the whole line) for anything but <c>true</c>.
/// </summary>
internal sealed class LenientBoolConverter : JsonConverter<bool>
{
    /// <summary>
    /// Reads the flag.
    /// </summary>
    /// <param name="reader">The reader positioned on the value.</param>
    /// <param name="typeToConvert">The target type.</param>
    /// <param name="options">The serializer options in effect.</param>
    /// <returns><see langword="true"/> only for a JSON <c>true</c>.</returns>
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var result = reader.TokenType == JsonTokenType.True;
        reader.Skip();

        return result;
    }

    /// <summary>
    /// Not supported; transcript rows are only ever deserialized.
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <param name="value">The value.</param>
    /// <param name="options">The serializer options in effect.</param>
    /// <exception cref="NotSupportedException">Always thrown.</exception>
    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Reads a decimal number, yielding <see langword="null"/> (instead of failing the whole line) for any other value.
/// </summary>
internal sealed class LenientDoubleConverter : JsonConverter<double?>
{
    /// <summary>
    /// Reads the number.
    /// </summary>
    /// <param name="reader">The reader positioned on the value.</param>
    /// <param name="typeToConvert">The target type.</param>
    /// <param name="options">The serializer options in effect.</param>
    /// <returns>The number, or <see langword="null"/>.</returns>
    public override double? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        double? result = reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out var value) ? value : null;
        reader.Skip();

        return result;
    }

    /// <summary>
    /// Not supported; transcript rows are only ever deserialized.
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <param name="value">The value.</param>
    /// <param name="options">The serializer options in effect.</param>
    /// <exception cref="NotSupportedException">Always thrown.</exception>
    public override void Write(Utf8JsonWriter writer, double? value, JsonSerializerOptions options)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Reads an integer that may be written as a JSON number or a numeric string, yielding <see langword="null"/>
/// (instead of failing the whole line) for any other value.
/// </summary>
internal sealed class LenientLongConverter : JsonConverter<long?>
{
    /// <summary>
    /// Reads the value, tolerating numbers with a fraction, numeric strings and any other token.
    /// </summary>
    /// <param name="reader">The reader positioned on the value.</param>
    /// <param name="typeToConvert">The target type.</param>
    /// <param name="options">The serializer options in effect.</param>
    /// <returns>The value, or <see langword="null"/> when it is not numeric.</returns>
    public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        long? result = null;
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                if (reader.TryGetInt64(out var integer))
                {
                    result = integer;
                }
                else if (reader.TryGetDouble(out var real) && real is >= -9.2233720368547758E+18 and < 9.2233720368547758E+18)
                {
                    result = (long)real;
                }

                break;
            case JsonTokenType.String when long.TryParse(reader.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                result = parsed;
                break;
        }

        reader.Skip();

        return result;
    }

    /// <summary>
    /// Not supported; transcript rows are only ever deserialized.
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <param name="value">The value.</param>
    /// <param name="options">The serializer options in effect.</param>
    /// <exception cref="NotSupportedException">Always thrown.</exception>
    public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Reads a string value, yielding <see langword="null"/> (instead of failing the whole line) when the value is any
/// other kind of JSON.
/// </summary>
internal sealed class LenientStringConverter : JsonConverter<string?>
{
    /// <summary>
    /// Reads the value, skipping anything that is not a string.
    /// </summary>
    /// <param name="reader">The reader positioned on the value.</param>
    /// <param name="typeToConvert">The target type.</param>
    /// <param name="options">The serializer options in effect.</param>
    /// <returns>The string, or <see langword="null"/>.</returns>
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var result = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        reader.Skip();

        return result;
    }

    /// <summary>
    /// Not supported; transcript rows are only ever deserialized.
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <param name="value">The value.</param>
    /// <param name="options">The serializer options in effect.</param>
    /// <exception cref="NotSupportedException">Always thrown.</exception>
    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Reads the <c>origin</c> of a user line, which is an object, yielding <see langword="null"/> for any other shape.
/// </summary>
internal sealed class LenientOriginConverter : JsonConverter<LineOrigin?>
{
    /// <summary>
    /// Reads the origin object, keeping only its <c>kind</c>.
    /// </summary>
    /// <param name="reader">The reader positioned on the value.</param>
    /// <param name="typeToConvert">The target type.</param>
    /// <param name="options">The serializer options in effect.</param>
    /// <returns>The origin, or <see langword="null"/>.</returns>
    public override LineOrigin? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();

            return null;
        }

        string? kind = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var isKind = reader.ValueTextEquals("kind"u8);
            reader.Read();
            if (isKind && reader.TokenType == JsonTokenType.String)
            {
                kind = reader.GetString();
            }

            reader.Skip();
        }

        return new() { Kind = kind };
    }

    /// <summary>
    /// Not supported; transcript rows are only ever deserialized.
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <param name="value">The value.</param>
    /// <param name="options">The serializer options in effect.</param>
    /// <exception cref="NotSupportedException">Always thrown.</exception>
    public override void Write(Utf8JsonWriter writer, LineOrigin? value, JsonSerializerOptions options)
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
