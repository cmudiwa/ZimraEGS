using Newtonsoft.Json;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ZimraEGS.ApiClient.Models;

namespace ZimraEGS.ApiClient.Helpers
{
    public class ApiHelper
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;

        public ApiHelper(PlatformType environmentType)
        {
            var baseUrl = Utilities.GetBaseUrl(environmentType);
            _baseUrl = baseUrl.EndsWith("/") ? baseUrl.TrimEnd('/') : baseUrl;
            _httpClient = new HttpClient();
        }

        public ApiHelper(string pfxBase64, PlatformType environmentType)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;

            var baseUrl = Utilities.GetBaseUrl(environmentType);
            _baseUrl = baseUrl.EndsWith("/") ? baseUrl.TrimEnd('/') : baseUrl;

            try
            {
                var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (message, cert, chain, sslPolicyErrors) =>
                    {
                        if (sslPolicyErrors != System.Net.Security.SslPolicyErrors.None)
                        {
                            Console.WriteLine($"SSL Policy Errors: {sslPolicyErrors}");
                        }
                        return true;
                    },

                    SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13
                };

                try
                {
                    var certificate = LoadCertificateFromPfx(pfxBase64, null);

                    handler.ClientCertificates.Add(certificate);

                    _httpClient = new HttpClient(handler)
                    {
                        Timeout = TimeSpan.FromSeconds(30)
                    };
                }
                catch (Exception certEx)
                {
                    Console.WriteLine($"Certificate Loading Error: {certEx.Message}");
                    throw;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"TLS Setup Error: {ex.Message}");
                throw; 
            }
        }

        private X509Certificate2 LoadCertificateFromPfx(string pfxBase64, string? password)
        {
            byte[] pfxBytes = Convert.FromBase64String(pfxBase64);

            var certificate = new X509Certificate2(pfxBytes, password, X509KeyStorageFlags.MachineKeySet);

            return certificate;
        }

        public async Task<ServerResponse> SendGetRequestAsync(string lastPath, int? deviceID, string deviceModelName, string deviceModelVersion)
        {
            if (string.IsNullOrWhiteSpace(lastPath) || !deviceID.HasValue)
                throw new ArgumentException("Invalid arguments provided for the API request.");

            string endpoint = $"{_baseUrl}/Device/v1/{deviceID}/{lastPath}";

            var requestMessage = new HttpRequestMessage(HttpMethod.Get, endpoint);

            requestMessage.Headers.Add("DeviceModelName", deviceModelName);
            requestMessage.Headers.Add("DeviceModelVersion", deviceModelVersion);

            try
            {
                using var response = await _httpClient.SendAsync(requestMessage);
                var responseContent = await response.Content.ReadAsStringAsync();

                return new ServerResponse(
                    endpoint,
                    response.StatusCode,
                    response.Headers,
                    responseContent
                );
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in SendGetRequestAsync: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"Inner Exception: {ex.InnerException.Message}");
                }
                throw new Exception($"An error occurred while calling the API: {ex.Message}", ex);
            }
        }

        public async Task<ServerResponse> VerifyTaxpayerInformationAsync(int deviceID, VerifyTaxpayerInformationRequest body)
        {
            string endpoint = $"{_baseUrl}/Public/v1/{deviceID}/VerifyTaxpayerInformation";

            string requestBody = JsonConvert.SerializeObject(body);

            var requestMessage = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
            };

            try
            {
                using var response = await _httpClient.SendAsync(requestMessage);
                var responseContent = await response.Content.ReadAsStringAsync();

                return new ServerResponse(
                    endpoint,
                    response.StatusCode,
                    response.Headers,
                    responseContent
                );
            }
            catch (Exception ex)
            {
                throw new Exception($"An error occurred while calling the API: {ex.Message}", ex);
            }
        }
        public async Task<ServerResponse> RegisterDeviceAsync(int? deviceID, string deviceModelName, string deviceModelVersion, RegisterDeviceRequest body)
        {
            string endpoint = $"{_baseUrl}/Public/v1/{deviceID}/RegisterDevice";

            string requestBody = JsonConvert.SerializeObject(body);

            var requestMessage = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
            };

            requestMessage.Headers.Add("DeviceModelName", deviceModelName);
            requestMessage.Headers.Add("DeviceModelVersion", deviceModelVersion);

            try
            {
                using var response = await _httpClient.SendAsync(requestMessage);
                var responseContent = await response.Content.ReadAsStringAsync();

                return new ServerResponse(
                    endpoint,
                    response.StatusCode,
                    response.Headers,
                    responseContent
                );
            }
            catch (Exception ex)
            {
                throw new Exception($"An error occurred while calling the API: {ex.Message}", ex);
            }
        }

        public async Task<ServerResponse> GetConfigAsync(int? deviceID, string deviceModelName, string deviceModelVersion)
        {
            string endpoint = $"{_baseUrl}/Device/v1/{deviceID}/GetConfig";

            var requestMessage = new HttpRequestMessage(HttpMethod.Get, endpoint);

            requestMessage.Headers.Add("DeviceModelName", deviceModelName);
            requestMessage.Headers.Add("DeviceModelVersion", deviceModelVersion);

            try
            {
                using var response = await _httpClient.SendAsync(requestMessage);
                var responseContent = await response.Content.ReadAsStringAsync();

                return new ServerResponse(
                    endpoint,
                    response.StatusCode,
                    response.Headers,
                    responseContent
                );
            }
            catch (Exception ex)
            {
                throw new Exception($"An error occurred while calling the API: {ex.Message}", ex);
            }
        }
        
        public async Task<ServerResponse> SendPostRequestAsync<TBody>(string lastPath, int? deviceID, string deviceModelName, string deviceModelVersion, TBody? body)
        {
            string endpoint = $"{_baseUrl}/Device/v1/{deviceID}/{lastPath}";

            string requestBody = JsonConvert.SerializeObject(body);

            var requestMessage = new HttpRequestMessage(HttpMethod.Post, endpoint);

            if (body != null)
            {
                requestMessage.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
            };

            requestMessage.Headers.Add("DeviceModelName", deviceModelName);
            requestMessage.Headers.Add("DeviceModelVersion", deviceModelVersion);

            try
            {
                using var response = await _httpClient.SendAsync(requestMessage);
                var responseContent = await response.Content.ReadAsStringAsync();

                return new ServerResponse(
                    endpoint,
                    response.StatusCode,
                    response.Headers,
                    responseContent
                );
            }
            catch (Exception ex)
            {
                throw new Exception($"An error occurred while calling the API: {ex.Message}", ex);
            }
        }

    }
}