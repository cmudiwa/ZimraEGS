using Newtonsoft.Json;
using ZimraEGS.ApiClient.Helpers;
using ZimraEGS.ApiClient.Models;

namespace ZimraEGS.Models
{
    public class CertificateInfo
    {
        public PlatformType IntegrationType { get; set; } = PlatformType.Simulation;
        public int DeviceID { get; set; } = 0;
        public string DeviceSerialNumber { get; set; } = "";
        public string ActivationKey { get; set; } = "";
        public string DeviceModelName { get; set; } = "Server";
        public string DeviceModelVersion { get; set; } = "v1";

        public string CommonName { get; set; } = string.Empty;
        public string TaxPayerName { get; set; } = string.Empty;
        public string TaxPayerTIN { get; set; } = "";
        public string VatNumber { get; set; } = "";
        public string DeviceBranchName { get; set; } = string.Empty;
        public string Province { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
        public string Street { get; set; } = string.Empty;
        public string HouseNo { get; set; } = string.Empty;

        public string PhoneNo { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;

        public string GeneratedCSR { get; set; } = string.Empty;
        public string PrivateKey { get; set; } = string.Empty;
        public string DeviceCertificate { get; set; }
        public string Base64Pfx { get; set; } = string.Empty;

        public int DeviceOperatingMode { get; set; }
        public string ApplicableTaxes { get; set; }
        public int TaxPayerDayMaxHrs { get; set; }
        public int TaxpayerDayEndNotificationHrs { get; set; }
        public string QrUrl { get; set; }

        [JsonProperty("CertificateValidTill")]
        [Newtonsoft.Json.JsonConverter(typeof(LocalDateTimeConverter))]
        public DateTimeOffset CertificateValidTill { get; set; }

        public string ApiBaseUrl => Utilities.GetBaseUrl(IntegrationType);
        public CertificateInfo()
        {
            DeviceID = 0;
            DeviceSerialNumber = "";
            DeviceModelName = "Server";
            DeviceModelVersion = "v1";
            ActivationKey = "";
        }
        public string ToBusinessAddress()
        {
            // Plain text, one item per line: Manager prints the address as text and the invoice theme
            // reads the VAT number from it to choose between "FISCAL TAX INVOICE" and "FISCAL INVOICE".
            var lines = new List<string>();
            if (!string.IsNullOrWhiteSpace(DeviceBranchName)) lines.Add(DeviceBranchName.Trim());
            var ids = new List<string>();
            if (!string.IsNullOrWhiteSpace(TaxPayerTIN)) ids.Add($"TIN: {TaxPayerTIN.Trim()}");
            if (!string.IsNullOrWhiteSpace(VatNumber)) ids.Add($"VAT No: {VatNumber.Trim()}");
            if (ids.Count > 0) lines.Add(string.Join("   ", ids));
            string address = string.Join(", ", new[] { HouseNo, Street, City, Province }
                .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase));
            if (address.Length > 0) lines.Add(address);
            if (!string.IsNullOrWhiteSpace(Email)) lines.Add(Email.Trim());
            if (!string.IsNullOrWhiteSpace(PhoneNo)) lines.Add(PhoneNo.Trim());
            return string.Join("\n", lines);
        }
    }

    public class CertInfoViewModel
    {
        public string BusinessDetailJson { get; set; }
        public string Api { get; set; }
        public string Token { get; set; }
        public string Referrer { get; set; }
        public CertificateInfo CertificateInfo { get; set; }
        public CertInfoViewModel() { }
    }
}
