using ZimraEGS.Data;
using ZimraEGS.Services;

namespace ZimraEGS.Models
{
    public class AccountFormModel
    {
        public string Email { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Phone { get; set; } = string.Empty;
        public string? ReturnUrl { get; set; }
        public string? Error { get; set; }
        /// <summary>Sign-up: the customer ticked "I have read the privacy notice".</summary>
        public bool Agree { get; set; }
    }

    public class AccountPageModel
    {
        public Account Account { get; set; } = new();
        public List<(Device device, BillingStatus status)> Devices { get; set; } = [];
        public List<SubscriptionPayment> Payments { get; set; } = [];
        public BillingOptions Options { get; set; } = new();
        public bool IsAdmin { get; set; }
        public string? Message { get; set; }
        public string? Error { get; set; }
        /// <summary>The privacy notice changed since the customer last agreed to it.</summary>
        public bool NeedsConsent { get; set; }
    }

    public class PrivacyPageModel
    {
        public PrivacyOptions Options { get; set; } = new();
        public BillingOptions Billing { get; set; } = new();
        public List<string> Missing { get; set; } = [];
    }

    public class AdminPrivacyModel
    {
        public PrivacyOptions Options { get; set; } = new();
        public List<string> Missing { get; set; } = [];
        public bool HttpsOnly { get; set; }
        public bool IsHttps { get; set; }
        public List<AuditEvent> Events { get; set; } = [];
        public string? Search { get; set; }
    }

    public class AdminPageModel
    {
        public List<Account> Accounts { get; set; } = [];
        public List<(Device device, BillingStatus status)> Devices { get; set; } = [];
        public string Filter { get; set; } = "attention";
        public string? Message { get; set; }
    }

    public class AdminDeviceModel
    {
        public Device Device { get; set; } = new();
        public BillingStatus Status { get; set; } = new(BillingState.Free, null, null);
        public List<SubscriptionPayment> Payments { get; set; } = [];
        public List<Account> Accounts { get; set; } = [];
        public string TaxpayerName { get; set; } = string.Empty;
        public BillingOptions Options { get; set; } = new();
        public string? Message { get; set; }
        public string? Error { get; set; }
    }

    public class AdminAccountModel
    {
        public Account Account { get; set; } = new();
        public List<(Device device, BillingStatus status)> Devices { get; set; } = [];
        public string? NewPassword { get; set; }
        public string? Message { get; set; }
    }
}
