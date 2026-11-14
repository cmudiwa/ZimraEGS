using Newtonsoft.Json;
using System.Net;
using System.Net.Http.Headers;

namespace ZimraEGS.ApiClient.Helpers
{
    public class ServerResponse
    {
        public string Endpoint { get; }
        public HttpStatusCode StatusCode { get; }
        public string ResponseContent { get; } = "{}";

        public bool IsSuccess => StatusCode == HttpStatusCode.OK;

        /// <summary>ZIMRA's clock at the time of the answer (HTTP Date header), when it was sent.</summary>
        public DateTimeOffset? ServerTime { get; init; }

        public ServerResponse(string endpoint, HttpStatusCode statusCode, string responseContent)
        {
            Endpoint = endpoint;
            StatusCode = statusCode;
            ResponseContent = responseContent;
        }

        /// <summary>A short, human readable description of a failed call, taken from the FDMS problem details.</summary>
        public string DescribeError()
        {
            try
            {
                var obj = Newtonsoft.Json.Linq.JObject.Parse(ResponseContent);
                var parts = new[] { (string?)obj["errorCode"], (string?)obj["title"], (string?)obj["detail"] }
                    .Where(x => !string.IsNullOrWhiteSpace(x));
                var text = string.Join(" - ", parts);
                if (!string.IsNullOrEmpty(text)) return $"{text} (HTTP {(int)StatusCode})";
            }
            catch (JsonException) { }

            var raw = string.IsNullOrWhiteSpace(ResponseContent) ? StatusCode.ToString() : ResponseContent;
            if (raw.Length > 400) raw = raw[..400];
            return $"{raw} (HTTP {(int)StatusCode})";
        }

        public T GetContentAs<T>()
        {
            try
            {
                var settings = new JsonSerializerSettings
                {
                    Converters = new List<JsonConverter> { new LocalDateTimeConverter() },
                    DateParseHandling = DateParseHandling.None // Ensure no default date parsing conflicts
                };

                return JsonConvert.DeserializeObject<T>(ResponseContent, settings);
            }
            catch (JsonException ex)
            {
                throw new Exception($"Error deserializing response content: {ex.Message}", ex);
            }
        }

        public string GetContentAsString()
        {
            return ResponseContent;
        }

        private static object? TryParse(string content)
        {
            try { return JsonConvert.DeserializeObject(content); }
            catch (JsonException) { return content; }
        }

        public string GetFullResponseAsJson()
        {
            var responseLog = new
            {
                Endpoint,
                StatusCode = (int)StatusCode,
                StatusDescription = StatusCode.ToString(),
                ResponseContent = TryParse(ResponseContent)
            };

            return JsonConvert.SerializeObject(responseLog, Formatting.Indented);
        }
    }

}