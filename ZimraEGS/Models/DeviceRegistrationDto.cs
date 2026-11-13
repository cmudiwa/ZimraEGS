namespace ZimraEGS.Controllers
{
    public partial class SetupController
    {
        public class DeviceRegistrationDto
        {
            public string IntegrationType { get; set; }
            public int DeviceID { get; set; }
            public string DeviceSerialNumber { get; set; }
            public string CommonName { get; set; }
            public string DeviceModelName { get; set; }
            public string DeviceModelVersion { get; set; }
            public string ActivationKey { get; set; }
        }

        public class VerifyTaxPayerDto
        {
            public string IntegrationType { get; set; }
            public int DeviceID { get; set; }
            public string DeviceSerialNumber { get; set; }
            public string ActivationKey { get; set; }
        }

        public class GetConfigDto
        {
            /// <summary>Link returned by device registration; identifies the device held on the server.</summary>
            public string DeviceLink { get; set; }
        }
    }
}
