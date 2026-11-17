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
        public HomeViewModel Server { get; set; } = new();
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

namespace ZimraEGS.Models
{
    public class ExcelPageModel
    {
        public List<(Data.Device device, Services.BillingStatus status)> Devices { get; set; } = [];
        public Data.Device? Selected { get; set; }
        public List<Data.ExcelBatch> Batches { get; set; } = [];
        public Services.BillingOptions BillingOptions { get; set; } = new();
        public int RetentionDays { get; set; }
        public string? Error { get; set; }
        public string? Message { get; set; }
    }

    public class ExcelConnectModel
    {
        public string Environment { get; set; } = "Simulation";
        public int DeviceID { get; set; }
        public string SerialNumber { get; set; } = string.Empty;
        public string ActivationKey { get; set; } = string.Empty;
        public int? DefaultMoneyType { get; set; }
        public string? Error { get; set; }
    }

    public class ExcelBatchModel
    {
        public Data.ExcelBatch Batch { get; set; } = new();
        public Data.Device Device { get; set; } = new();
        public Services.BillingStatus Billing { get; set; } = new(Services.BillingState.Free, null, null);
        public Services.BillingOptions BillingOptions { get; set; } = new();
        public List<string> FileProblems { get; set; } = [];
        public List<Services.ExcelCheck> Checks { get; set; } = [];
        public List<Services.ExcelResult>? Results { get; set; }
    }

    public static class MoneyTypeNames
    {
        public static readonly (int value, string name)[] All =
        [
            (0, "Cash"), (1, "Card"), (2, "Mobile wallet"), (3, "Coupon"), (4, "Credit"), (5, "Bank transfer"), (6, "Other"),
        ];
    }
}
