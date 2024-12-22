using Newtonsoft.Json;
using System.Net;
using System.Net.Http.Headers;

namespace ZimraEGS.ApiClient.Helpers
{
    public class ServerResponse
    {
        public string Endpoint { get; }
        public HttpStatusCode StatusCode { get; }
        public HttpResponseHeaders Headers { get; }
        public string ResponseContent { get; } = "{}";

        public ServerResponse(string endpoint, HttpStatusCode statusCode, HttpResponseHeaders headers, string responseContent)
        {
            Endpoint = endpoint;
            StatusCode = statusCode;
            Headers = headers;
            ResponseContent = responseContent;
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

        public string GetFullResponseAsJson()
        {
            var responseLog = new
            {
                Endpoint,
                StatusCode = (int)StatusCode,
                StatusDescription = StatusCode.ToString(),
                ResponseContent = JsonConvert.DeserializeObject(ResponseContent)
            };

            return JsonConvert.SerializeObject(responseLog, Formatting.Indented);
        }
    }

}