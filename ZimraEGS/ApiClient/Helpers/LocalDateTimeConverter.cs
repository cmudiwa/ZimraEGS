using Newtonsoft.Json;
using System.Globalization;

namespace ZimraEGS.ApiClient.Helpers
{
    
    public class LocalDateTimeConverter : JsonConverter
    {
        //private static readonly TimeZoneInfo SouthAfricaTimeZone = TimeZoneInfo.FindSystemTimeZoneById("South Africa Standard Time");


        private static readonly string[] SupportedDateFormats = new[] {
            "MM/dd/yyyy h:mm:ss tt",   // US-style AM/PM format
            "yyyy-MM-ddTHH:mm:ss",     // Local time without 'Z'
            "yyyy-MM-dd h:mm:ss tt",   // Local time  AM/PM format
            "yyyy-MM-ddTHH:mm:sszzz",  // ISO with timezone offset
            "M/d/yyyy h:mm:ss tt",     // Single-digit day/month
            "MM/dd/yyyy HH:mm:ss",     // US-style 24-hour format
            "dd-MM-yyyy HH:mm:ss",     // European-style 24-hour format
            "yyyy-MM-dd HH:mm:ss",
            "yyyy/MM/dd HH:mm:ss",     // Alternative format
            "M/d/yyyy h:mm:ss tt",     // Fallback single-digit format
            "yyyy-MM-ddTHH:mm:ss.fff", // ISO with milliseconds
            "yyyy-MM-dd HH:mm:ss.fff"  // Alternative milliseconds
        };

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            if (value is DateTimeOffset dateTimeOffset)
            {
                writer.WriteValue(dateTimeOffset.ToOffset(TimeSpan.FromHours(2)).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
            }
            else
            {
                writer.WriteNull();
            }
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            // Newtonsoft may already have parsed the token into a date, depending on DateParseHandling.
            if (reader.Value is DateTimeOffset parsedOffset)
            {
                return parsedOffset;
            }
            if (reader.Value is DateTime parsedDate)
            {
                return parsedDate.Kind == DateTimeKind.Utc
                    ? new DateTimeOffset(parsedDate).ToOffset(TimeSpan.FromHours(2))
                    : new DateTimeOffset(DateTime.SpecifyKind(parsedDate, DateTimeKind.Unspecified), TimeSpan.FromHours(2));
            }

            var value = reader.Value?.ToString();

            if (string.IsNullOrEmpty(value))
            {
                return objectType == typeof(DateTimeOffset) ? default(DateTimeOffset) : null;
            }

            // FDMS sends Zimbabwe local time (UTC+2) without an offset. Interpret it as such
            // regardless of the time zone of the machine this runs on.
            if (DateTimeOffset.TryParseExact(value, "yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var withOffset))
            {
                return withOffset;
            }

            if (DateTime.TryParseExact(value, SupportedDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var localDateTime)
                || DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out localDateTime))
            {
                return new DateTimeOffset(DateTime.SpecifyKind(localDateTime, DateTimeKind.Unspecified), TimeSpan.FromHours(2));
            }

            throw new JsonSerializationException($"Unable to parse date: {value}");
        }


        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(DateTimeOffset) || objectType == typeof(DateTimeOffset?);
        }
    }
}