using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System.Net;
using ZimraEGS.ApiClient.Helpers;
using ZimraEGS.ApiClient.Models;
using ZimraEGS.Data;
using ZimraEGS.Services;
using ZimraEGS.Helpers;
using ZimraEGS.Models;


namespace ZimraEGS.Controllers
{
    public partial class SetupController : Controller
    {

        private readonly ILogger<SetupController> _logger;
        private readonly DeviceStore _store;
        private readonly FiscalDbContext _db;
        private readonly FiscalService _fiscal;
        private readonly IFdmsClientFactory _clientFactory;

        public SetupController(ILogger<SetupController> logger, DeviceStore store, FiscalDbContext db, FiscalService fiscal, IFdmsClientFactory clientFactory)
        {
            _logger = logger;
            _store = store;
            _db = db;
            _fiscal = fiscal;
            _clientFactory = clientFactory;
        }

        [HttpPost("Update")]
        public IActionResult UpdateCustomField([FromForm] Dictionary<string, string> formData)
        {
            try
            {
                var Referrer = Utils.GetValue(formData, "Referrer");
                var FormKey = Utils.GetValue(formData, "Key");
                var Data = Utils.GetValue(formData, "Data");
                var Api = Utils.GetValue(formData, "Api");
                var Token = Utils.GetValue(formData, "Token");

                _logger.LogInformation("Setup API Diakses oleh : {0} IP address: {1}", Api, HttpContext.Connection.RemoteIpAddress?.ToString() ?? "Unknown");

                var BusinessDetailJson = RelayDataHelper.GetValueJson(Data, "BusinessDetails");

                if (!string.IsNullOrEmpty(FormKey))
                {
                    SetupViewModel setupViewModel = new SetupViewModel
                    {
                        Referrer = Referrer,
                        Api = Api,
                        Token = Token,
                        BusinessDetailsJson = BusinessDetailJson
                    };

                    return View("UpdateBusinessData", setupViewModel);
                }

                return View("index", "Home");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kesalahan saat memproses setup api");
                throw new Exception(ex.Message);
            }
        }

        [HttpGet("Setup/UpdateBusinessData")]
        public IActionResult UpdateBusinessData()
        {
            var svmJson = TempData["SetupViewModel"] as string;
            if (string.IsNullOrEmpty(svmJson))
            {
                return Content("No TempData found.");
            }

            var viewModel = JsonConvert.DeserializeObject<SetupViewModel>(svmJson);
            return View(viewModel);
        }

        public IActionResult Register()
        {
            SetupViewModel model = null;

            if (TempData["SetupViewModel"] != null)
            {
                model = JsonConvert.DeserializeObject<SetupViewModel>(TempData["SetupViewModel"].ToString());
            }

            if (model == null)
            {
                return RedirectToAction("Index");
            }
            return View("Index", model); // This will return the Index view with the model
        }


        [HttpGet("setup/GetCfData")]
        public IActionResult CustomFieldJson()
        {
            try
            {
                string jsonData = Utils.ReadEmbeddedResource("cfData.json");
                return Content(jsonData, "application/json");
            }
            catch (Exception ex)
            {
                return BadRequest($"Failed to load resource: {ex.Message}");
            }
        }

        [HttpPost("setup/verifytaxpayer")]
        public async Task<IActionResult> VerifyTaxPayerInformation([FromBody] VerifyTaxPayerDto verifyTaxPayerDto)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(new { Errors = "Invalid input data" });
            }

            try
            {
                VerifyTaxpayerInformationRequest verifyTaxpayerInformationRequest = new VerifyTaxpayerInformationRequest
                {
                    ActivationKey = verifyTaxPayerDto.ActivationKey,
                    DeviceSerialNo = verifyTaxPayerDto.DeviceSerialNumber
                };

                PlatformType integrationType = (PlatformType)Enum.Parse(typeof(PlatformType), verifyTaxPayerDto.IntegrationType);
                using ApiHelper apiHelper = _clientFactory.CreatePublicClient(integrationType);
                ServerResponse serverResponse = await apiHelper.VerifyTaxpayerInformationAsync(
                    verifyTaxPayerDto.DeviceID,
                    verifyTaxpayerInformationRequest
                );

                if (serverResponse.StatusCode == HttpStatusCode.OK)
                {
                    return Ok(serverResponse.ResponseContent);
                }

                return BadRequest(new { Errors = serverResponse.GetContentAsString() });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Error = $"Internal server error: {ex.Message}" });
            }
        }

        [HttpPost("setup/registerdevice")]
        public async Task<IActionResult> RegisterDeviceAsync([FromBody] DeviceRegistrationDto deviceRegistration)
        {
            if (!ModelState.IsValid)
            {
                var errors = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).ToList();
                return BadRequest(new { Errors = errors });
            }

            if (deviceRegistration == null || string.IsNullOrEmpty(deviceRegistration.CommonName))
            {
                return BadRequest(new { Errors = "Common name is required." });
            }

            try
            {
                string errMessage = string.Empty;

                var (generatedCsr, privateKey) = RSA_CryptoHelper.GenerateCSR(deviceRegistration.CommonName);

                // Register Device
                RegisterDeviceRequest registerDeviceRequest = new RegisterDeviceRequest
                {
                    ActivationKey = deviceRegistration.ActivationKey,
                    CertificateRequest = generatedCsr
                };

                PlatformType integrationType = (PlatformType)Enum.Parse(typeof(PlatformType), deviceRegistration.IntegrationType);

                using ApiHelper apiHelper = _clientFactory.CreatePublicClient(integrationType);
                ServerResponse serverResponse = await apiHelper.RegisterDeviceAsync(deviceRegistration.DeviceID, deviceRegistration.DeviceModelName, deviceRegistration.DeviceModelVersion, registerDeviceRequest);

                if (serverResponse.StatusCode == HttpStatusCode.OK)
                {
                    var deviceCertificate = Utilities.CleanBase64String(serverResponse.GetContentAs<RegisterDeviceResponse>().Certificate);
                    string cleanKey = Utilities.CleanBase64String(privateKey);
                    string pfx = Utilities.GeneratePfx(cleanKey, deviceCertificate, (string)null);

                    // The private key stays on the server. The browser only receives a link to the device.
                    var device = await _store.FindAsync(integrationType, deviceRegistration.DeviceID);
                    if (device == null)
                    {
                        device = new Device { DeviceID = deviceRegistration.DeviceID, Environment = integrationType };
                        _db.Devices.Add(device);
                    }

                    device.SerialNumber = deviceRegistration.DeviceSerialNumber ?? string.Empty;
                    device.ModelName = deviceRegistration.DeviceModelName;
                    device.ModelVersion = deviceRegistration.DeviceModelVersion;
                    device.ProtectedPrivateKey = _store.Protect(cleanKey);
                    device.ProtectedPfx = _store.Protect(pfx);
                    device.DeviceCertificate = deviceCertificate;
                    device.GeneratedCsr = Utilities.CleanBase64String(generatedCsr);
                    device.IsActive = false;
                    await _db.SaveChangesAsync();

                    string deviceLink = await _store.IssueLinkAsync(device);

                    var response = new
                    {
                        GeneratedCsr = device.GeneratedCsr,
                        DeviceCertificate = deviceCertificate,
                        DeviceLink = deviceLink
                    };

                    return Ok(response);
                }
                else
                {
                    errMessage = serverResponse.GetFullResponseAsJson();
                }

                return BadRequest(new { Errors = errMessage });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Errors = "Error generating CSR: " + ex.Message });
            }
        }

        [HttpPost("setup/getconfig")]
        public async Task<IActionResult> GetDeviceConfig([FromBody] GetConfigDto getConfigDTO)
        {
            if (!ModelState.IsValid)
            {
                var errors = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).ToList();
                return BadRequest(new { Errors = errors });
            }

            var device = await _store.FindByLinkAsync(getConfigDTO?.DeviceLink, requireActive: false);
            if (device == null)
            {
                return BadRequest(new { Errors = "The device is not registered on this server yet. Register the device first." });
            }

            try
            {
                string errMessage = string.Empty;

                using var client = _clientFactory.CreateDeviceClient(device.Environment, device.DeviceID, device.ModelName, device.ModelVersion, _store.Unprotect(device.ProtectedPfx));

                ServerResponse serverResponse = await client.GetConfigAsync();

                if (serverResponse.StatusCode == HttpStatusCode.OK)
                {
                    GetConfigResponse getConfigResponse = serverResponse.GetContentAs<GetConfigResponse>();

                    device.ConfigJson = JsonConvert.SerializeObject(getConfigResponse);
                    device.ConfigFetchedUtc = DateTime.UtcNow;
                    device.CertificateValidTillUtc = getConfigResponse.CertificateValidTill.UtcDateTime;
                    await _db.SaveChangesAsync();

                    var response = new
                    {
                        DeviceOperatingMode = getConfigResponse.DeviceOperatingMode,
                        ApplicableTaxes = string.Join("#", getConfigResponse.ApplicableTaxes.Select(x => $"{x.TaxID} - {x.TaxName} - {x.TaxPercent}")),
                        TaxPayerDayMaxHrs = getConfigResponse.TaxPayerDayMaxHrs,
                        TaxpayerDayEndNotificationHrs = getConfigResponse.TaxpayerDayEndNotificationHrs,
                        CertificateValidTill = getConfigResponse.CertificateValidTill,
                        QrUrl = getConfigResponse.QrUrl
                    };

                    return Ok(response);
                }
                else
                {
                    errMessage = serverResponse.GetContentAsString();
                }

                Console.WriteLine(errMessage);

                return BadRequest(new { Errors = errMessage });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Errors = "Error Get Device Configuration: " + ex.Message });
            }
        }


        [HttpPost("setup/savecertificate")]
        public async Task<IActionResult> SaveCertificate(SetupViewModel viewModel)
        {
            var device = await _store.FindByLinkAsync(viewModel.DeviceLink, requireActive: false);
            if (device == null)
            {
                return BadRequest("The device is not registered on this server. Start the registration again from Manager.");
            }

            CertificateInfo certificateInfo = viewModel.CertificateInfo;

            // Key material never comes from the browser; whatever was posted in these fields is ignored.
            certificateInfo.PrivateKey = string.Empty;
            certificateInfo.Base64Pfx = string.Empty;
            certificateInfo.DeviceCertificate = string.Empty;
            certificateInfo.GeneratedCSR = string.Empty;
            certificateInfo.DeviceID = device.DeviceID;
            certificateInfo.IntegrationType = device.Environment;

            _store.ApplyCertificateInfo(device, certificateInfo);
            device.IsActive = true;
            await _db.SaveChangesAsync();

            _logger.LogInformation("Device {DeviceID} ({Environment}) registered", device.DeviceID, device.Environment);

            // Start from the business details Manager relayed, so nothing the user entered there is lost.
            string businessDetails = string.IsNullOrWhiteSpace(viewModel.BusinessDetailsJson) ? "{}" : viewModel.BusinessDetailsJson;
            businessDetails = RelayDataHelper.UpdateOrCreateField(businessDetails, "Name", certificateInfo.TaxPayerName ?? string.Empty);
            businessDetails = RelayDataHelper.UpdateOrCreateField(businessDetails, "Address", certificateInfo.ToBusinessAddress());

            var call = ManagerWriteBack.BusinessLink(viewModel.Api ?? string.Empty, businessDetails, viewModel.DeviceLink);

            viewModel.BusinessDetails = call.Payload;
            viewModel.BusinessDetailsJson = call.Payload;
            viewModel.CertificateInfo = DeviceStore.WithoutSecrets(certificateInfo);
            viewModel.CertificateInfo.DeviceCertificate = device.DeviceCertificate;
            viewModel.IsFileReady = true;

            return View("index", viewModel);
        }
    }
}
