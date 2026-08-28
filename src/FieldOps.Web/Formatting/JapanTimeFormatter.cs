using System.Globalization;

namespace FieldOps.Web.Formatting;

public static class JapanTimeFormatter
{
    public const string ZoneLabel = "Asia/Tokyo";

    private static readonly TimeZoneInfo JapanTimeZone = FindJapanTimeZone();

    public static string FormatUtc(DateTime utcValue)
    {
        DateTime japanValue = ToJapanDateTime(utcValue);
        return japanValue.ToString("yyyy年M月d日 H:mm", CultureInfo.GetCultureInfo("ja-JP"));
    }

    public static DateOnly ToJapanDate(DateTime utcValue)
    {
        return DateOnly.FromDateTime(ToJapanDateTime(utcValue));
    }

    public static TimeOnly ToJapanTime(DateTime utcValue)
    {
        return TimeOnly.FromDateTime(ToJapanDateTime(utcValue));
    }

    public static DateTime ToUtc(DateOnly date, TimeOnly time)
    {
        DateTime local = date.ToDateTime(time, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(local, JapanTimeZone);
    }

    private static DateTime ToJapanDateTime(DateTime utcValue)
    {
        if (utcValue.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("The timestamp must be UTC.", nameof(utcValue));
        }

        return TimeZoneInfo.ConvertTimeFromUtc(utcValue, JapanTimeZone);
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