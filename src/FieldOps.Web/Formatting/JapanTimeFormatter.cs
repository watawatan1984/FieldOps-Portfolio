using System.Globalization;

namespace FieldOps.Web.Formatting;

public static class JapanTimeFormatter
{
    public const string ZoneLabel = "Asia/Tokyo";

    private static readonly TimeZoneInfo JapanTimeZone = FindJapanTimeZone();

    public static string FormatUtc(DateTime utcValue)
    {
        if (utcValue.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("The timestamp must be UTC.", nameof(utcValue));
        }

        DateTime japanValue = TimeZoneInfo.ConvertTimeFromUtc(utcValue, JapanTimeZone);
        return japanValue.ToString("yyyy-MM-dd HH:mm 'JST'", CultureInfo.InvariantCulture);
    }

    private static TimeZoneInfo FindJapanTimeZone()
    {
        foreach (string timeZoneId in new[] { "Asia/Tokyo", "Tokyo Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        throw new TimeZoneNotFoundException(
            "Neither the IANA nor Windows identifier for the Asia/Tokyo time zone is available.");
    }
}