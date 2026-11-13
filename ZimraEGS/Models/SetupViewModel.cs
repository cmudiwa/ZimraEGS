namespace ZimraEGS.Models
{
    /// <summary>What the setup wizard needs from the relay: where Manager is and how to write to it.</summary>
    public class SetupViewModel
    {
        public string Referrer { get; set; } = string.Empty;
        public string BusinessDetailsJson { get; set; } = string.Empty;
        public string Api { get; set; } = string.Empty;
        public string Token { get; set; } = string.Empty;
    }
}
