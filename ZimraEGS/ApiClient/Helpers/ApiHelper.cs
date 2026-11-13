using Newtonsoft.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ZimraEGS.ApiClient.Models;

namespace ZimraEGS.ApiClient.Helpers
{
    public class FdmsOptions
    {
        /// <summary>Overrides the FDMS base URL for both environments. Meant for testing against a stand-in.</summary>
        public string? BaseUrlOverride { get; set; }

        /// <summary>
        /// Turns off validation of the FDMS server certificate. Leave this off; only switch it on if the
        /// host cannot build the ZIMRA certificate chain and you accept the risk.
        /// </summary>
        public bool SkipServerCertificateValidation { get; set; }

        public int TimeoutSeconds { get; set; } = 30;
    }

    /// <summary>The FDMS calls a registered device makes. Abstracted so the flow can be tested without ZIMRA.</summary>
    public interface IFdmsClient : IDisposable
    {
        Task<ServerResponse> GetStatusAsync();
        Task<ServerResponse> GetConfigAsync();
        Task<ServerResponse> OpenDayAsync(OpenDayRequest request);
        Task<ServerResponse> SubmitReceiptAsync(SubmitReceiptRequest request);
        Task<ServerResponse> CloseDayAsync(CloseDayRequest request);
        Task<ServerResponse> IssueCertificateAsync(IssueCertificateRequest request);
    }

    public interface IFdmsClientFactory
    {
        IFdmsClient CreateDeviceClient(PlatformType environment, int deviceID, string modelName, string modelVersion, string pfxBase64);
        ApiHelper CreatePublicClient(PlatformType environment);
    }

    public class FdmsClientFactory : IFdmsClientFactory
    {
        private readonly FdmsOptions _options;

        public FdmsClientFactory(Microsoft.Extensions.Options.IOptions<FdmsOptions> options)
        {
            _options = options.Value;
        }

        public IFdmsClient CreateDeviceClient(PlatformType environment, int deviceID, string modelName, string modelVersion, string pfxBase64)
        {
            return new ApiHelper(pfxBase64, environment, _options) { DeviceID = deviceID, DeviceModelName = modelName, DeviceModelVersion = modelVersion };
        }

        public ApiHelper CreatePublicClient(PlatformType environment)
        {
            return new ApiHelper(environment, _options);
        }
    }

    public class ApiHelper : IFdmsClient
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;
        private readonly X509Certificate2? _certificate;

        public int DeviceID { get; init; }
        public string DeviceModelName { get; init; } = "Server";
        public string DeviceModelVersion { get; init; } = "v1";

        /// <summary>Client for the public endpoints, which need no device certificate.</summary>
        public ApiHelper(PlatformType environmentType, FdmsOptions? options = null)
        {
            options ??= new FdmsOptions();
            _baseUrl = ResolveBaseUrl(environmentType, options);
            _httpClient = new HttpClient(CreateHandler(options, null)) { Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds) };
        }

        /// <summary>Client for the device endpoints, authenticated with the device certificate.</summary>
        public ApiHelper(string pfxBase64, PlatformType environmentType, FdmsOptions? options = null)
        {
            options ??= new FdmsOptions();
            _baseUrl = ResolveBaseUrl(environmentType, options);

            // EphemeralKeySet is not supported on macOS; the default key set works everywhere.
            _certificate = new X509Certificate2(Convert.FromBase64String(pfxBase64), (string?)null,
                OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet);

            _httpClient = new HttpClient(CreateHandler(options, _certificate)) { Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds) };
        }

        private static string ResolveBaseUrl(PlatformType environmentType, FdmsOptions options)
        {
            var baseUrl = string.IsNullOrWhiteSpace(options.BaseUrlOverride) ? Utilities.GetBaseUrl(environmentType) : options.BaseUrlOverride;
            return baseUrl.TrimEnd('/');
        }

        private static HttpClientHandler CreateHandler(FdmsOptions options, X509Certificate2? certificate)
        {
            var handler = new HttpClientHandler
            {
                SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13
            };

            if (options.SkipServerCertificateValidation)
            {
                handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            }

            if (certificate != null)
            {
                handler.ClientCertificateOptions = ClientCertificateOption.Manual;
                handler.ClientCertificates.Add(certificate);
            }

            return handler;
        }

        public Task<ServerResponse> GetStatusAsync() => SendAsync(HttpMethod.Get, $"Device/v1/{DeviceID}/GetStatus", null, true);
        public Task<ServerResponse> GetConfigAsync() => SendAsync(HttpMethod.Get, $"Device/v1/{DeviceID}/GetConfig", null, true);
        public Task<ServerResponse> OpenDayAsync(OpenDayRequest request) => SendAsync(HttpMethod.Post, $"Device/v1/{DeviceID}/OpenDay", request, true);
        public Task<ServerResponse> SubmitReceiptAsync(SubmitReceiptRequest request) => SendAsync(HttpMethod.Post, $"Device/v1/{DeviceID}/SubmitReceipt", request, true);
        public Task<ServerResponse> CloseDayAsync(CloseDayRequest request) => SendAsync(HttpMethod.Post, $"Device/v1/{DeviceID}/CloseDay", request, true);
        public Task<ServerResponse> IssueCertificateAsync(IssueCertificateRequest request) => SendAsync(HttpMethod.Post, $"Device/v1/{DeviceID}/IssueCertificate", request, true);

        public Task<ServerResponse> VerifyTaxpayerInformationAsync(int deviceID, VerifyTaxpayerInformationRequest body)
            => SendAsync(HttpMethod.Post, $"Public/v1/{deviceID}/VerifyTaxpayerInformation", body, false);

        public Task<ServerResponse> RegisterDeviceAsync(int deviceID, string deviceModelName, string deviceModelVersion, RegisterDeviceRequest body)
            => SendAsync(HttpMethod.Post, $"Public/v1/{deviceID}/RegisterDevice", body, true, deviceModelName, deviceModelVersion);

        private async Task<ServerResponse> SendAsync(HttpMethod method, string path, object? body, bool deviceHeaders, string? modelName = null, string? modelVersion = null)
        {
            string endpoint = $"{_baseUrl}/{path}";

            using var requestMessage = new HttpRequestMessage(method, endpoint);

            if (body != null)
            {
                requestMessage.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
            }

            if (deviceHeaders)
            {
                requestMessage.Headers.Add("DeviceModelName", modelName ?? DeviceModelName);
                requestMessage.Headers.Add("DeviceModelVersion", modelVersion ?? DeviceModelVersion);
            }

            using var response = await _httpClient.SendAsync(requestMessage);
            var responseContent = await response.Content.ReadAsStringAsync();

            return new ServerResponse(endpoint, response.StatusCode, responseContent);
        }

        public void Dispose()
        {
            _httpClient.Dispose();
            _certificate?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
