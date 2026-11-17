using Newtonsoft.Json;
using ZimraEGS.ApiClient.Enums;
using ZimraEGS.ApiClient.Helpers;
using ZimraEGS.ApiClient.Models;
using ZimraEGS.Data;
using ZimraEGS.Helpers;
using ZimraEGS.Models;

namespace ZimraEGS.Services
{
    /// <summary>
    /// Registers a fiscal device with ZIMRA and fetches its configuration. Used by the Manager panel and by the
    /// Excel pages. Running it again with the same activation key does not register the device a second time.
    /// </summary>
    public class DeviceRegistration
    {
        private readonly DeviceStore _store;
        private readonly FiscalDbContext _db;
        private readonly IFdmsClientFactory _clientFactory;

        public DeviceRegistration(DeviceStore store, FiscalDbContext db, IFdmsClientFactory clientFactory)
        {
            _store = store;
            _db = db;
            _clientFactory = clientFactory;
        }

        /// <returns>The active device and its details, or null and why not. ZIMRA being unreachable throws
        /// HttpRequestException or TaskCanceledException.</returns>
        public async Task<(Device? device, CertificateInfo? info, string? error)> RegisterAsync(PlatformType environment, int deviceId,
            string serialNumber, string activationKey, string? modelNameIn, string? modelVersionIn, int? defaultMoneyType)
        {
            string modelName = string.IsNullOrWhiteSpace(modelNameIn) ? "Server" : modelNameIn.Trim();
            string modelVersion = string.IsNullOrWhiteSpace(modelVersionIn) ? "v1" : modelVersionIn.Trim();
            string activationHash = DeviceStore.HashToken(activationKey.Trim());

            var device = await _store.FindAsync(environment, deviceId);

            // Setup was run before with the same activation key (for example the save in Manager failed):
            // the device is already registered, so only hand out a new link.
            bool alreadyRegistered = device != null && !string.IsNullOrEmpty(device.ProtectedPfx) &&
                string.Equals(device.ActivationKeyHash, activationHash, StringComparison.Ordinal);

            if (!alreadyRegistered)
            {
                string commonName = $"ZIMRA-{serialNumber.Trim()}-{deviceId.ToString().PadLeft(10, '0')}";
                var (csr, privateKey) = RSA_CryptoHelper.GenerateCSR(commonName);

                using ApiHelper publicClient = _clientFactory.CreatePublicClient(environment);
                ServerResponse registered = await publicClient.RegisterDeviceAsync(deviceId, modelName, modelVersion,
                    new RegisterDeviceRequest { ActivationKey = activationKey.Trim(), CertificateRequest = csr });

                if (!registered.IsSuccess)
                {
                    return (null, null, "ZIMRA did not register the device: " + registered.DescribeError());
                }

                string certificate = Utilities.CleanBase64String(registered.GetContentAs<RegisterDeviceResponse>().Certificate);
                string cleanKey = Utilities.CleanBase64String(privateKey);

                if (device == null)
                {
                    device = new Device { DeviceID = deviceId, Environment = environment };
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

            device!.SerialNumber = serialNumber.Trim();
            device.ModelName = modelName;
            device.ModelVersion = modelVersion;
            if (defaultMoneyType is { } moneyType && Enum.IsDefined(typeof(MoneyType), moneyType))
            {
                device.DefaultMoneyType = moneyType;
            }
            await _db.SaveChangesAsync();

            using var client = _clientFactory.CreateDeviceClient(device.Environment, device.DeviceID, device.ModelName, device.ModelVersion, _store.Unprotect(device.ProtectedPfx));
            ServerResponse configResponse = await client.GetConfigAsync();
            if (!configResponse.IsSuccess)
            {
                return (null, null, "The device is registered, but ZIMRA did not return its configuration: " + configResponse.DescribeError() + " Try again to retry.");
            }

            GetConfigResponse config = configResponse.GetContentAs<GetConfigResponse>();
            var info = InfoFromConfig(device, config);

            device.ConfigJson = JsonConvert.SerializeObject(config);
            device.ConfigFetchedUtc = DateTime.UtcNow;
            _store.ApplyCertificateInfo(device, info);
            device.IsActive = true;
            await _db.SaveChangesAsync();

            return (device, info, null);
        }

        public static CertificateInfo InfoFromConfig(Device device, GetConfigResponse config)
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
    }
}
