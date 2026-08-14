using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AgenTally.Core.Collectors.KimiCode;

internal static class KimiCodeTextNormalizer
{
    public static string? BuildPromptPreview(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var source = new StringBuilder(256);
        foreach (JsonElement part in input.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? type = ReadBoundedString(part, "type", 64);
            string? value = type switch
            {
                "text" when part.TryGetProperty("text", out JsonElement text) &&
                    text.ValueKind == JsonValueKind.String => text.GetString(),
                "image" or "image_url" => "[图片]",
                "audio" or "input_audio" => "[音频]",
                _ => null
            };
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (source.Length > 0)
            {
                source.Append(' ');
            }

            source.Append(value);
        }

        return Normalize(source.ToString());
    }

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        const int maximumScalars = 120;
        var normalized = new StringBuilder(maximumScalars);
        bool pendingSpace = false;
        int scalarCount = 0;
        foreach (Rune rune in value.EnumerateRunes())
        {
            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            if (Rune.IsWhiteSpace(rune) ||
                category is UnicodeCategory.Control or UnicodeCategory.Format)
            {
                pendingSpace = normalized.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                if (scalarCount + 1 >= maximumScalars)
                {
                    break;
                }

                normalized.Append(' ');
                scalarCount++;
            }

            pendingSpace = false;
            if (scalarCount >= maximumScalars)
            {
                break;
            }

            normalized.Append(rune.ToString());
            scalarCount++;
        }

        return normalized.Length == 0 ? null : normalized.ToString();
    }

    public static string? ReadBoundedString(
        JsonElement value,
        string propertyName,
        int maximumCharacters)
    {
        if (!value.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? result = property.GetString();
        return result is { Length: > 0 } &&
               result.Length <= maximumCharacters &&
               !string.IsNullOrWhiteSpace(result) &&
               !result.Any(char.IsControl)
            ? result
            : null;
    }
}
