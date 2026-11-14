using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System.Net;
using ZimraEGS.ApiClient.Enums;
using ZimraEGS.ApiClient.Helpers;
using ZimraEGS.ApiClient.Models;
using ZimraEGS.Data;
using ZimraEGS.Helpers;
using ZimraEGS.Models;
using ZimraEGS.Services;

namespace ZimraEGS.Controllers
{
    public class SetupController : Controller
    {
        private readonly ILogger<SetupController> _logger;
        private readonly DeviceStore _store;
        private readonly FiscalDbContext _db;
        private readonly IFdmsClientFactory _clientFactory;

        public SetupController(ILogger<SetupController> logger, DeviceStore store, FiscalDbContext db, IFdmsClientFactory clientFactory)
        {
            _logger = logger;
            _store = store;
            _db = db;
            _clientFactory = clientFactory;
        }

        public class DeviceSetupRequest
        {
            public string IntegrationType { get; set; } = "Simulation";
            public int DeviceID { get; set; }
            public string DeviceSerialNumber { get; set; } = string.Empty;
            public string ActivationKey { get; set; } = string.Empty;
            public string? DeviceModelName { get; set; }
            public string? DeviceModelVersion { get; set; }
            public int? DefaultMoneyType { get; set; }
            public string? Api { get; set; }
            public string? BusinessDetailsJson { get; set; }
        }

        /// <summary>The setup wizard. Reached from the relay when the business has no device yet.</summary>
        public IActionResult Register()
        {
            if (TempData["SetupViewModel"] is string json && JsonConvert.DeserializeObject<SetupViewModel>(json) is { } model)
            {
                return View("Index", model);
            }
            return RedirectToAction("Index", "Home");
        }

        /// <summary>Step 1: ask ZIMRA who the device belongs to, so the user can confirm before registering.</summary>
        [HttpPost("setup/verifytaxpayer")]
        public async Task<IActionResult> VerifyTaxPayerInformation([FromBody] DeviceSetupRequest request)
        {
            if (!TryValidate(request, out var environment, out string error))
            {
                return BadRequest(new { Error = error });
            }

            try
            {
                using ApiHelper client = _clientFactory.CreatePublicClient(environment);
                ServerResponse response = await client.VerifyTaxpayerInformationAsync(request.DeviceID, new VerifyTaxpayerInformationRequest
                {
                    ActivationKey = request.ActivationKey,
                    DeviceSerialNo = request.DeviceSerialNumber
                });

                if (!response.IsSuccess)
                {
                    return BadRequest(new { Error = "ZIMRA did not recognise this device: " + response.DescribeError() });
                }

                return Content(response.ResponseContent, "application/json");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return StatusCode(502, new { Error = "ZIMRA could not be reached: " + ex.Message });
            }
        }

        /// <summary>
        /// Step 2: everything else in one call. Generates the key on the server, registers the device with ZIMRA,
        /// fetches its configuration and returns what the browser has to store in Manager.
        /// </summary>
        [HttpPost("setup/complete")]
        public async Task<IActionResult> Complete([FromBody] DeviceSetupRequest request)
        {
            if (!TryValidate(request, out var environment, out string error))
            {
                return BadRequest(new { Error = error });
            }

            try
            {
                string modelName = string.IsNullOrWhiteSpace(request.DeviceModelName) ? "Server" : request.DeviceModelName.Trim();
                string modelVersion = string.IsNullOrWhiteSpace(request.DeviceModelVersion) ? "v1" : request.DeviceModelVersion.Trim();
                string activationHash = DeviceStore.HashToken(request.ActivationKey.Trim());

                var device = await _store.FindAsync(environment, request.DeviceID);

                // Setup was run before with the same activation key (for example the save in Manager failed):
                // the device is already registered, so only hand out a new link.
                bool alreadyRegistered = device != null && !string.IsNullOrEmpty(device.ProtectedPfx) &&
                    string.Equals(device.ActivationKeyHash, activationHash, StringComparison.Ordinal);

                if (!alreadyRegistered)
                {
                    string commonName = $"ZIMRA-{request.DeviceSerialNumber.Trim()}-{request.DeviceID.ToString().PadLeft(10, '0')}";
                    var (csr, privateKey) = RSA_CryptoHelper.GenerateCSR(commonName);

                    using ApiHelper publicClient = _clientFactory.CreatePublicClient(environment);
                    ServerResponse registered = await publicClient.RegisterDeviceAsync(request.DeviceID, modelName, modelVersion,
                        new RegisterDeviceRequest { ActivationKey = request.ActivationKey.Trim(), CertificateRequest = csr });

                    if (!registered.IsSuccess)
                    {
                        return BadRequest(new { Error = "ZIMRA did not register the device: " + registered.DescribeError() });
                    }

                    string certificate = Utilities.CleanBase64String(registered.GetContentAs<RegisterDeviceResponse>().Certificate);
                    string cleanKey = Utilities.CleanBase64String(privateKey);

                    if (device == null)
                    {
                        device = new Device { DeviceID = request.DeviceID, Environment = environment };
                        _db.Devices.Add(device);
                    }

                    // The private key is created here and never leaves the server.
                    device.ProtectedPrivateKey = _store.Protect(cleanKey);
                    device.ProtectedPfx = _store.Protect(Utilities.GeneratePfx(cleanKey, certificate, null));
                    device.DeviceCertificate = certificate;
                    device.GeneratedCsr = Utilities.CleanBase64String(csr);
                    device.ActivationKeyHash = activationHash;
                    device.IsActive = false;
                }

                device!.SerialNumber = request.DeviceSerialNumber.Trim();
                device.ModelName = modelName;
                device.ModelVersion = modelVersion;
                if (request.DefaultMoneyType is { } moneyType && Enum.IsDefined(typeof(MoneyType), moneyType))
                {
                    device.DefaultMoneyType = moneyType;
                }
                await _db.SaveChangesAsync();

                using var client = _clientFactory.CreateDeviceClient(device.Environment, device.DeviceID, device.ModelName, device.ModelVersion, _store.Unprotect(device.ProtectedPfx));
                ServerResponse configResponse = await client.GetConfigAsync();
                if (!configResponse.IsSuccess)
                {
                    return BadRequest(new { Error = "The device is registered, but ZIMRA did not return its configuration: " + configResponse.DescribeError() + " Click the button again to retry." });
                }

                GetConfigResponse config = configResponse.GetContentAs<GetConfigResponse>();
                var info = InfoFromConfig(device, config);

                device.ConfigJson = JsonConvert.SerializeObject(config);
                device.ConfigFetchedUtc = DateTime.UtcNow;
                _store.ApplyCertificateInfo(device, info);
                device.IsActive = true;
                await _db.SaveChangesAsync();

                string deviceLink = await _store.IssueLinkAsync(device);
                _logger.LogInformation("Device {DeviceID} ({Environment}) set up", device.DeviceID, device.Environment);

                // Start from the business details Manager relayed, so nothing the user entered there is lost.
                string businessDetails = string.IsNullOrWhiteSpace(request.BusinessDetailsJson) ? "{}" : request.BusinessDetailsJson;
                businessDetails = RelayDataHelper.UpdateOrCreateField(businessDetails, "Name", info.TaxPayerName ?? string.Empty);
                businessDetails = RelayDataHelper.UpdateOrCreateField(businessDetails, "Address", info.ToBusinessAddress());

                var payload = new
                {
                    DeviceLink = deviceLink,
                    TaxPayerName = info.TaxPayerName,
                    Business = ManagerWriteBack.BusinessLink(request.Api ?? string.Empty, businessDetails, deviceLink),
                    Records = ManagerSetupData.Build(device),
                    // For the extension page, which writes through Manager's API bridge.
                    ApiCalls = ManagerSetupData.BuildApiCalls(device)
                        .Append(ManagerWriteBack.BusinessLinkPatch(deviceLink, info.TaxPayerName, info.ToBusinessAddress())),
                };
                return Content(JsonConvert.SerializeObject(payload), "application/json");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return StatusCode(502, new { Error = "ZIMRA could not be reached: " + ex.Message + " Click the button again to retry." });
            }
        }

        private static CertificateInfo InfoFromConfig(Device device, GetConfigResponse config)
        {
            return new CertificateInfo
            {
                IntegrationType = device.Environment,
                DeviceID = device.DeviceID,
                DeviceSerialNumber = device.SerialNumber,
                DeviceModelName = device.ModelName,
                DeviceModelVersion = device.ModelVersion,
                CommonName = $"ZIMRA-{device.SerialNumber}-{device.DeviceID.ToString().PadLeft(10, '0')}",
                TaxPayerName = config.TaxPayerName,
                TaxPayerTIN = config.TaxPayerTIN,
                VatNumber = config.VatNumber ?? string.Empty,
                DeviceBranchName = config.DeviceBranchName,
                Province = config.DeviceBranchAddress?.Province ?? string.Empty,
                City = config.DeviceBranchAddress?.City ?? string.Empty,
                Street = config.DeviceBranchAddress?.Street ?? string.Empty,
                HouseNo = config.DeviceBranchAddress?.HouseNo ?? string.Empty,
                PhoneNo = config.DeviceBranchContacts?.PhoneNo ?? string.Empty,
                Email = config.DeviceBranchContacts?.Email ?? string.Empty,
                DeviceOperatingMode = (int)config.DeviceOperatingMode,
                ApplicableTaxes = string.Join("#", config.ApplicableTaxes.Select(x => $"{x.TaxID} - {x.TaxName} - {x.TaxPercent}")),
                TaxPayerDayMaxHrs = config.TaxPayerDayMaxHrs,
                TaxpayerDayEndNotificationHrs = config.TaxpayerDayEndNotificationHrs,
                QrUrl = config.QrUrl,
                CertificateValidTill = config.CertificateValidTill,
            };
        }

        private static bool TryValidate(DeviceSetupRequest? request, out PlatformType environment, out string error)
        {
            environment = PlatformType.Simulation;
            error = string.Empty;

            if (request == null) { error = "No data received."; return false; }
            if (!Enum.TryParse(request.IntegrationType, out environment)) { error = "Choose Test or Production."; return false; }
            if (request.DeviceID <= 0) { error = "Enter the Device ID from ZIMRA."; return false; }
            if (string.IsNullOrWhiteSpace(request.DeviceSerialNumber)) { error = "Enter the device Serial Number."; return false; }
            if (string.IsNullOrWhiteSpace(request.ActivationKey)) { error = "Enter the Activation Key."; return false; }
            return true;
        }

        /// <summary>Custom fields, theme and tax codes to create in Manager, for a device that is already set up.</summary>
        [HttpPost("setup/records")]
        public async Task<IActionResult> Records([FromForm] string deviceLink)
        {
            var device = await _store.FindByLinkAsync(deviceLink);
            if (device == null) return BadRequest(new { Error = "Unknown device." });
            return Content(ManagerSetupData.Build(device).ToString(Formatting.None), "application/json");
        }
    }
}
