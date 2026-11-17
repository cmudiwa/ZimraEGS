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
        private readonly BillingService _billing;
        private readonly DeviceRegistration _registration;

        public SetupController(ILogger<SetupController> logger, DeviceStore store, FiscalDbContext db, IFdmsClientFactory clientFactory, BillingService billing, DeviceRegistration registration)
        {
            _registration = registration;
            _billing = billing;
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
            /// <summary>The account a live device is billed to.</summary>
            public string? AccountEmail { get; set; }
            public string? AccountPassword { get; set; }
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

            // A live device is billed to an account: check it before anything is registered with ZIMRA.
            Account? account = null;
            if (environment == PlatformType.Production && _billing.Options.Enabled)
            {
                string signUp = $"{Request.Scheme}://{Request.Host}{Request.PathBase}/account/signup";
                if (string.IsNullOrWhiteSpace(request.AccountEmail) || string.IsNullOrEmpty(request.AccountPassword))
                {
                    return BadRequest(new { Error = $"A live device needs an account. Enter your account's e-mail and password, or create an account at {signUp}" });
                }
                var (found, accountError) = await _billing.AuthenticateAsync(request.AccountEmail, request.AccountPassword);
                if (found == null) return BadRequest(new { Error = accountError + $" (Create an account at {signUp})" });
                account = found;
            }

            try
            {
                var (device, info, failure) = await _registration.RegisterAsync(environment, request.DeviceID, request.DeviceSerialNumber,
                    request.ActivationKey, request.DeviceModelName, request.DeviceModelVersion, request.DefaultMoneyType);
                if (device == null) return BadRequest(new { Error = failure });

                if (account != null && device.AccountId != account.Id)
                {
                    await _billing.LinkDeviceAsync(device, account);
                }

                string deviceLink = await _store.IssueLinkAsync(device);
                _logger.LogInformation("Device {DeviceID} ({Environment}) set up", device.DeviceID, device.Environment);

                // Start from the business details Manager relayed, so nothing the user entered there is lost.
                string businessDetails = string.IsNullOrWhiteSpace(request.BusinessDetailsJson) ? "{}" : request.BusinessDetailsJson;
                businessDetails = RelayDataHelper.UpdateOrCreateField(businessDetails, "Name", info!.TaxPayerName ?? string.Empty);
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
