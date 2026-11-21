// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Buffers;
using System.Text.Json;

namespace Nalix.Observability.Internal;

/// <summary>
/// Re-emits a JSON object with every array capped at a number of items and long strings shortened, so a report
/// whose per-item lists are unbounded (workers, instances, middleware) still fits the response budget.
/// </summary>
/// <remarks>
/// One forward-only pass, no DOM. When anything was dropped the root object gains <c>"_truncated": true</c>,
/// which dashboards show next to the section.
/// </remarks>
internal static class JsonTrimmer
{
    /// <summary>The marker property added to a trimmed section's root object.</summary>
    public const string TruncatedProperty = "_truncated";

    private const int MaxStringChars = 512;
    private static readonly JsonWriterOptions s_options = new() { Indented = false, SkipValidation = true };

    /// <summary>
    /// Writes <paramref name="json"/> to <paramref name="output"/> keeping at most <paramref name="maxItems"/>
    /// items per array.
    /// </summary>
    /// <returns><see langword="true"/> when something was dropped or shortened.</returns>
    public static bool Trim(ReadOnlySpan<byte> json, int maxItems, IBufferWriter<byte> output)
    {
        Utf8JsonReader reader = new(json, new JsonReaderOptions { MaxDepth = 64 });
        using Utf8JsonWriter writer = new(output, s_options);

        // Per depth: -1 for objects, otherwise the number of items already kept in that array.
        Span<int> items = stackalloc int[66];
        int depth = 0;
        bool trimmed = false;

        while (reader.Read())
        {
            JsonTokenType token = reader.TokenType;
            bool isValue = token is not (JsonTokenType.PropertyName or JsonTokenType.EndObject or JsonTokenType.EndArray);

            // A value directly inside an array counts as one item; past the cap it is skipped whole.
            if (isValue && depth > 0 && items[depth - 1] >= 0)
            {
                if (items[depth - 1] >= maxItems)
                {
                    trimmed = true;
                    reader.Skip();
                    continue;
                }

                items[depth - 1]++;
            }

            switch (token)
            {
                case JsonTokenType.StartObject:
                    writer.WriteStartObject();
                    items[depth++] = -1;
                    break;

                case JsonTokenType.StartArray:
                    writer.WriteStartArray();
                    items[depth++] = 0;
                    break;

                case JsonTokenType.EndObject:
                    depth--;
                    if (depth == 0 && trimmed)
                    {
                        writer.WriteBoolean(TruncatedProperty, true);
                    }

                    writer.WriteEndObject();
                    break;

                case JsonTokenType.EndArray:
                    depth--;
                    writer.WriteEndArray();
                    break;

                case JsonTokenType.PropertyName:
                    if (reader.ValueIsEscaped)
                    {
                        writer.WritePropertyName(reader.GetString()!);
                    }
                    else
                    {
                        writer.WritePropertyName(reader.ValueSpan);
                    }

                    break;

                case JsonTokenType.String:
                    string text = reader.GetString()!;
                    if (text.Length > MaxStringChars)
                    {
                        text = string.Concat(text.AsSpan(0, MaxStringChars), "…");
                        trimmed = true;
                    }

                    writer.WriteStringValue(text);
                    break;

                case JsonTokenType.Number:
                    writer.WriteRawValue(reader.ValueSpan, skipInputValidation: true);
                    break;

                case JsonTokenType.True:
                case JsonTokenType.False:
                    writer.WriteBooleanValue(token == JsonTokenType.True);
                    break;

                case JsonTokenType.Null:
                    writer.WriteNullValue();
                    break;

                case JsonTokenType.None:
                case JsonTokenType.Comment:
                default:
                    break;
            }
        }

        writer.Flush();
        return trimmed;
    }
}
