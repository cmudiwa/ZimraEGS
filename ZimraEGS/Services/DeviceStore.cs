using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using System.Security.Cryptography;
using System.Text;
using ZimraEGS.ApiClient.Enums;
using ZimraEGS.ApiClient.Models;
using ZimraEGS.Data;
using ZimraEGS.Helpers;
using ZimraEGS.Models;

namespace ZimraEGS.Services
{
    /// <summary>
    /// What Manager keeps in the business custom field instead of the certificate and private key:
    /// a reference to the device held by this server plus a secret that proves the business may use it.
    /// </summary>
    public record DeviceLink(PlatformType Environment, int DeviceID, string Token)
    {
        private const string Prefix = "ZEGS-LINK:";

        public static bool IsLink(string? raw) => raw != null && raw.StartsWith(Prefix, StringComparison.Ordinal);

        public static DeviceLink? Parse(string? raw)
        {
            if (!IsLink(raw)) return null;
            var parts = raw![Prefix.Length..].Split(':');
            if (parts.Length != 3) return null;
            if (!Enum.TryParse(parts[0], out PlatformType environment)) return null;
            if (!int.TryParse(parts[1], out int deviceID)) return null;
            if (string.IsNullOrWhiteSpace(parts[2])) return null;
            return new DeviceLink(environment, deviceID, parts[2]);
        }

        public override string ToString() => $"{Prefix}{Environment}:{DeviceID}:{Token}";
    }

    public class DeviceStore
    {
        private readonly FiscalDbContext _db;
        private readonly IDataProtector _protector;
        private readonly ILogger<DeviceStore> _logger;

        public DeviceStore(FiscalDbContext db, IDataProtectionProvider protectionProvider, ILogger<DeviceStore> logger)
        {
            _db = db;
            _protector = protectionProvider.CreateProtector("ZimraEGS.DeviceSecrets.v1");
            _logger = logger;
        }

        public string Protect(string secret) => _protector.Protect(secret);
        public string Unprotect(string protectedSecret) => _protector.Unprotect(protectedSecret);

        public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

        public static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

        private static bool TokenMatches(Device device, string token)
        {
            return !string.IsNullOrEmpty(device.LinkTokenHash) &&
                CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(device.LinkTokenHash),
                    Encoding.ASCII.GetBytes(HashToken(token)));
        }

        public Task<Device?> FindAsync(PlatformType environment, int deviceID)
        {
            return _db.Devices.FirstOrDefaultAsync(d => d.DeviceID == deviceID && d.Environment == environment);
        }

        /// <summary>Returns the device a link refers to, or null when the link is unknown or its secret is wrong.</summary>
        public async Task<Device?> FindByLinkAsync(string? rawLink, bool requireActive = true)
        {
            var link = DeviceLink.Parse(rawLink);
            if (link == null) return null;

            var device = await FindAsync(link.Environment, link.DeviceID);
            if (device == null || !TokenMatches(device, link.Token)) return null;
            if (requireActive && !device.IsActive) return null;
            return device;
        }

        /// <summary>
        /// Finds the device for a relay request. A business still carrying the old certificate blob is
        /// imported into the database on first contact, together with its open fiscal day.
        /// </summary>
        public async Task<Device?> ResolveAsync(RelayData relay)
        {
            if (DeviceLink.IsLink(relay.DeviceLinkRaw))
            {
                return await FindByLinkAsync(relay.DeviceLinkRaw);
            }

            var legacy = relay.LegacyCertificateInfo;
            if (legacy == null || legacy.DeviceID == 0 || string.IsNullOrEmpty(legacy.PrivateKey) || string.IsNullOrEmpty(legacy.Base64Pfx))
            {
                return null;
            }

            var device = await FindAsync(legacy.IntegrationType, legacy.DeviceID);
            if (device != null)
            {
                // Already imported. Whoever holds the device's private key is allowed to use it.
                bool sameKey = CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(Unprotect(device.ProtectedPrivateKey)),
                    Encoding.UTF8.GetBytes(legacy.PrivateKey));
                return sameKey && device.IsActive ? device : null;
            }

            device = new Device
            {
                DeviceID = legacy.DeviceID,
                Environment = legacy.IntegrationType,
                LinkTokenHash = string.Empty,
                IsActive = true,
            };
            ApplyCertificateInfo(device, legacy);

            var reference = relay.LegacyBusinessReference;
            if (reference != null)
            {
                device.FiscalDayStatus = reference.FiscalDayStatus;
                device.FiscalDayNo = reference.LastFiscalDayNo;
                device.FiscalDayOpened = reference.FiscalDayOpened == default ? null : TimeZoneHelper.ToFdmsString(reference.FiscalDayOpened);
                device.LastReceiptCounter = reference.LastReceiptCounter;
                device.LastReceiptGlobalNo = reference.LastReceiptGlobalNo;
                device.LastReceiptHash = reference.LastReceiptHash ?? string.Empty;

                if (relay.LegacyFiscalDaySummary != null)
                {
                    device.CarriedSummaryJson = JsonConvert.SerializeObject(relay.LegacyFiscalDaySummary);
                    device.CarriedSummaryFiscalDayNo = reference.LastFiscalDayNo;
                }
            }

            _db.Devices.Add(device);
            await _db.SaveChangesAsync();
            _logger.LogInformation("Imported device {DeviceID} ({Environment}) from Manager custom fields", device.DeviceID, device.Environment);
            return device;
        }

        /// <summary>Copies registration details into the device row. Key material is encrypted; the rest is kept as JSON.</summary>
        public void ApplyCertificateInfo(Device device, CertificateInfo info)
        {
            device.SerialNumber = info.DeviceSerialNumber ?? string.Empty;
            device.ModelName = string.IsNullOrEmpty(info.DeviceModelName) ? "Server" : info.DeviceModelName;
            device.ModelVersion = string.IsNullOrEmpty(info.DeviceModelVersion) ? "v1" : info.DeviceModelVersion;

            if (!string.IsNullOrEmpty(info.PrivateKey)) device.ProtectedPrivateKey = Protect(info.PrivateKey);
            if (!string.IsNullOrEmpty(info.Base64Pfx)) device.ProtectedPfx = Protect(info.Base64Pfx);
            if (!string.IsNullOrEmpty(info.DeviceCertificate)) device.DeviceCertificate = info.DeviceCertificate;
            if (!string.IsNullOrEmpty(info.GeneratedCSR)) device.GeneratedCsr = info.GeneratedCSR;
            if (info.CertificateValidTill != default) device.CertificateValidTillUtc = info.CertificateValidTill.UtcDateTime;

            device.InfoJson = JsonConvert.SerializeObject(WithoutSecrets(info));
            device.UpdatedUtc = DateTime.UtcNow;
        }

        public static CertificateInfo WithoutSecrets(CertificateInfo info)
        {
            var copy = JsonConvert.DeserializeObject<CertificateInfo>(JsonConvert.SerializeObject(info))!;
            copy.PrivateKey = string.Empty;
            copy.Base64Pfx = string.Empty;
            copy.ActivationKey = string.Empty;
            return copy;
        }

        public static CertificateInfo GetInfo(Device device)
        {
            return JsonConvert.DeserializeObject<CertificateInfo>(device.InfoJson ?? "{}") ?? new CertificateInfo();
        }

        public static GetConfigResponse? GetConfig(Device device)
        {
            if (string.IsNullOrEmpty(device.ConfigJson)) return null;
            try { return JsonConvert.DeserializeObject<GetConfigResponse>(device.ConfigJson); }
            catch (JsonException) { return null; }
        }

        public async Task SetDefaultMoneyTypeAsync(Device device, int? moneyType)
        {
            device.DefaultMoneyType = moneyType;
            device.UpdatedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        /// <summary>Issues a fresh link secret for the device and returns the link to store in Manager.</summary>
        public async Task<string> IssueLinkAsync(Device device)
        {
            string token = NewToken();
            device.LinkTokenHash = HashToken(token);
            device.UpdatedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return new DeviceLink(device.Environment, device.DeviceID, token).ToString();
        }
    }
}
