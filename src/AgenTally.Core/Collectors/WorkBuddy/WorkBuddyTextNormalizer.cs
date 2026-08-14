using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AgenTally.Core.Collectors.WorkBuddy;

internal sealed record WorkBuddyPromptRead(
    string? Preview,
    bool IsInternalContinuation);

internal static class WorkBuddyTextNormalizer
{
    private const string SystemReminderOpen = "<system-reminder";
    private const string SystemReminderClose = "</system-reminder>";

    public static WorkBuddyPromptRead ReadPromptPreview(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Array)
        {
            return new WorkBuddyPromptRead(null, false);
        }

        var source = new StringBuilder(256);
        bool removedSystemReminder = false;
        bool hasUserVisibleContent = false;
        foreach (JsonElement part in content.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? type = ReadBoundedString(part, "type", 64);
            bool removed = false;
            string? value = type switch
            {
                "input_text" or "text" when
                    part.TryGetProperty("text", out JsonElement text) &&
                    text.ValueKind == JsonValueKind.String =>
                        RemoveSystemReminderBlocks(
                            text.GetString(),
                            out removed),
                "image" or "input_image" or "image_url" => "[图片]",
                "audio" or "input_audio" => "[音频]",
                _ => null
            };
            if (type is "input_text" or "text")
            {
                removedSystemReminder |= removed;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            hasUserVisibleContent = true;
            if (source.Length > 0)
            {
                source.Append(' ');
            }

            source.Append(value);
        }

        return new WorkBuddyPromptRead(
            Normalize(source.ToString()),
            removedSystemReminder && !hasUserVisibleContent);
    }

    private static string? RemoveSystemReminderBlocks(
        string? value,
        out bool removed)
    {
        removed = false;
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        int openingIndex = 0;
        while (openingIndex < value.Length && char.IsWhiteSpace(value[openingIndex]))
        {
            openingIndex++;
        }

        if (!value.AsSpan(openingIndex).StartsWith(
                SystemReminderOpen,
                StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        int boundary = openingIndex + SystemReminderOpen.Length;
        if (boundary < value.Length &&
            value[boundary] != '>' &&
            !char.IsWhiteSpace(value[boundary]))
        {
            return value;
        }

        removed = true;
        int openingEnd = value.IndexOf('>', boundary);
        if (openingEnd < 0)
        {
            return string.Empty;
        }

        int closingIndex = value.IndexOf(
            SystemReminderClose,
            openingEnd + 1,
            StringComparison.OrdinalIgnoreCase);
        return closingIndex < 0
            ? string.Empty
            : value[(closingIndex + SystemReminderClose.Length)..];
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
