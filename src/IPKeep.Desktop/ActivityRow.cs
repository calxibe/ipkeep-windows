using System.Globalization;
using System.Text.RegularExpressions;

namespace IPKeep.Desktop;

// Presentation only: preserve the on-disk log and its original timestamp/offset.
public sealed partial record ActivityRow(string TimestampText, string TimestampDetails, string Level, string Message, string RawLine)
{
    private const string LogTimestampFormat = "yyyy-MM-dd HH:mm:ss zzz";

    public static ActivityRow Parse(string line, TimeZoneInfo? timeZone = null, CultureInfo? culture = null)
    {
        string raw = line.TrimEnd('\r', '\n');
        var match = LogLinePattern().Match(raw);
        if (!match.Success || !TryTimestamp(match.Groups["time"].Value, out var timestamp))
            return new("—", "Timestamp unavailable", "—", raw, raw);

        timeZone ??= TimeZoneInfo.Local;
        culture ??= CultureInfo.CurrentCulture;
        var local = TimeZoneInfo.ConvertTime(timestamp, timeZone);
        string message = TimestampPattern().Replace(match.Groups["message"].Value, part =>
            TryTimestamp(part.Value, out var value)
                ? Format(TimeZoneInfo.ConvertTime(value, timeZone), culture)
                : part.Value);
        return new(Format(local, culture), $"{local.ToString(LogTimestampFormat, CultureInfo.InvariantCulture)} ({timeZone.Id})",
            match.Groups["level"].Value, message, raw);
    }

    public bool Matches(string filter) => RawLine.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || TimestampText.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || Message.Contains(filter, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => $"{TimestampText} {Level} {Message}";

    private static string Format(DateTimeOffset value, CultureInfo culture) => value.ToString("dd MMM, HH:mm:ss", culture);
    private static bool TryTimestamp(string value, out DateTimeOffset timestamp) =>
        DateTimeOffset.TryParseExact(value, LogTimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp);

    [GeneratedRegex(@"^(?<time>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} [+-]\d{2}:\d{2})[ \t]+(?<level>[A-Z]+)[ \t]+(?<message>.*)$")]
    private static partial Regex LogLinePattern();

    [GeneratedRegex(@"\b\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} [+-]\d{2}:\d{2}\b")]
    private static partial Regex TimestampPattern();
}
