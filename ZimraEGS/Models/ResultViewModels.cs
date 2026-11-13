using ZimraEGS.ApiClient.Models;
using ZimraEGS.Services;

namespace ZimraEGS.Models
{
    /// <summary>Shown after an invoice was recorded by ZIMRA (or was found to be recorded already).</summary>
    public class ReceiptResultViewModel
    {
        public string Referrer { get; set; } = string.Empty;
        public string Token { get; set; } = string.Empty;
        public string DeviceLink { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;
        public bool AlreadyFiscalised { get; set; }
        public string InvoiceNo { get; set; } = string.Empty;
        public string ReceiptType { get; set; } = string.Empty;
        public string ReceiptDate { get; set; } = string.Empty;
        public string Currency { get; set; } = string.Empty;
        public decimal Total { get; set; }
        public int FiscalDayNo { get; set; }
        public int ReceiptGlobalNo { get; set; }
        public string QrCode { get; set; } = string.Empty;
        public string VerificationCode { get; set; } = string.Empty;
        public List<ValidationError> ValidationErrors { get; set; } = [];

        public string FiscalDayStatus { get; set; } = string.Empty;
        public string? LastCloseDayError { get; set; }
        public int? DefaultMoneyType { get; set; }

        /// <summary>Updates the browser still has to store in Manager. Empty for invoices fiscalised before the upgrade.</summary>
        public List<ManagerApiCall> ManagerCalls { get; set; } = [];

        /// <summary>Go straight back to Manager once the updates are stored.</summary>
        public bool AutoReturn { get; set; }
    }

    public class ProblemViewModel
    {
        public string Referrer { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public List<string> Problems { get; set; } = [];

        /// <summary>Set when the device is known, so its settings can be changed from the page.</summary>
        public string DeviceLink { get; set; } = string.Empty;
        public int? DefaultMoneyType { get; set; }
    }

    public class DayResultViewModel
    {
        public string Referrer { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public bool Success { get; set; }
        public string? Hint { get; set; }
        public string Message { get; set; } = string.Empty;
        public int FiscalDayNo { get; set; }
    }
}
