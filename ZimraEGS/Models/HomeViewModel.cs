namespace ZimraEGS.Models
{
    /// <summary>What the start page shows. Deliberately no taxpayer details: the page needs no sign-in.</summary>
    public class HomeViewModel
    {
        public string Version { get; set; } = string.Empty;
        public string ExtensionEndpoint { get; set; } = string.Empty;
        public bool IsHttps { get; set; }
        public bool IsLocal { get; set; }
        public int TestDevices { get; set; }
        public int LiveDevices { get; set; }
        public int OpenFiscalDays { get; set; }
        public bool DatabaseOk { get; set; } = true;
        public bool AutoCloseEnabled { get; set; }
        public string AutoCloseTime { get; set; } = string.Empty;
        public int HsCodeCount { get; set; }
    }
}
