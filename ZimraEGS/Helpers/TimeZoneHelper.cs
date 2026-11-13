using System.Globalization;

namespace ZimraEGS.Helpers
{
    /// <summary>
    /// FDMS works in Zimbabwe local time (Central Africa Time, UTC+2, no daylight saving).
    /// A fixed offset is used so the result does not depend on the host's time zone database.
    /// </summary>
    public static class TimeZoneHelper
    {
        public static readonly TimeSpan ZimbabweOffset = TimeSpan.FromHours(2);
        public const string FdmsDateTimeFormat = "yyyy-MM-ddTHH:mm:ss";

        public static DateTimeOffset GetSouthAfricaTime()
        {
            return DateTimeOffset.UtcNow.ToOffset(ZimbabweOffset);
        }

        public static string GetSouthAfricaTimeString(string format = FdmsDateTimeFormat)
        {
            return GetSouthAfricaTime().ToString(format, CultureInfo.InvariantCulture);
        }

        public static string ToFdmsString(DateTimeOffset value)
        {
            return value.ToOffset(ZimbabweOffset).ToString(FdmsDateTimeFormat, CultureInfo.InvariantCulture);
        }

        /// <summary>Parses a Zimbabwe local time string written by <see cref="ToFdmsString"/>.</summary>
        public static DateTimeOffset? ParseFdmsString(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            {
                return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Unspecified), ZimbabweOffset);
            }
            return null;
        }
    }
}
