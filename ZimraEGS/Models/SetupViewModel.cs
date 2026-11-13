namespace ZimraEGS.Models
{
    public class SetupViewModel
    {
        public string Referrer { get; set; }
        public string BusinessDetails { get; set; } = string.Empty;
        public string BusinessDetailsJson { get; set; } = string.Empty;
        public string Api { get; set; }
        public string Token { get; set; }
        /// <summary>Reference to the device stored on the server. Replaces the certificate and key in Manager.</summary>
        public string DeviceLink { get; set; } = string.Empty;
        public bool IsFileReady { get; set; } = false;
        public string FileContent { get; set; } = string.Empty; 
        public string Filename { get; set; } = string.Empty;
        public CertificateInfo CertificateInfo { get; set; } = new CertificateInfo();
    }
}
