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
                writer.WriteValue(dateTimeOffset.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
            }
            else
            {
                writer.WriteNull();
            }
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            var value = reader.Value?.ToString();

            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            if (DateTime.TryParseExact(value, SupportedDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var localDateTime))
            {
                return new DateTimeOffset(localDateTime, TimeZoneInfo.Local.GetUtcOffset(localDateTime));
            }

            throw new JsonSerializationException($"Unable to parse date: {value}");
        }


        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(DateTimeOffset);
        }
    }
}