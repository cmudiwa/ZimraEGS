using Microsoft.EntityFrameworkCore;
using ZimraEGS.ApiClient.Enums;
using ZimraEGS.ApiClient.Models;

namespace ZimraEGS.Data
{
    /// <summary>
    /// One registered ZIMRA fiscal device. Holds the device identity, its secrets (encrypted at rest)
    /// and the receipt chain position, so the chain no longer depends on Manager custom fields.
    /// </summary>
    public class Device
    {
        public int Id { get; set; }

        public int DeviceID { get; set; }
        public PlatformType Environment { get; set; }
        public string SerialNumber { get; set; } = string.Empty;
        public string ModelName { get; set; } = "Server";
        public string ModelVersion { get; set; } = "v1";

        /// <summary>SHA-256 of the link token that Manager presents on every relay.</summary>
        public string LinkTokenHash { get; set; } = string.Empty;

        /// <summary>False until registration has been completed with GetConfig.</summary>
        public bool IsActive { get; set; }

        // Secrets, protected with ASP.NET Data Protection.
        public string ProtectedPrivateKey { get; set; } = string.Empty;
        public string ProtectedPfx { get; set; } = string.Empty;

        public string DeviceCertificate { get; set; } = string.Empty;
        public string GeneratedCsr { get; set; } = string.Empty;
        public DateTime? CertificateValidTillUtc { get; set; }

        /// <summary>Taxpayer and branch details (CertificateInfo without any key material), as JSON.</summary>
        public string InfoJson { get; set; } = "{}";

        /// <summary>Last GetConfig response from FDMS, as JSON.</summary>
        public string? ConfigJson { get; set; }
        public DateTime? ConfigFetchedUtc { get; set; }

        // Chain position.
        public FiscalDayStatus FiscalDayStatus { get; set; } = FiscalDayStatus.FiscalDayClosed;
        public int FiscalDayNo { get; set; }
        /// <summary>Fiscal day open time as sent to FDMS (Zimbabwe local time, yyyy-MM-ddTHH:mm:ss).</summary>
        public string? FiscalDayOpened { get; set; }
        public int LastReceiptCounter { get; set; }
        public int LastReceiptGlobalNo { get; set; }
        public string LastReceiptHash { get; set; } = string.Empty;

        /// <summary>
        /// Day totals carried over from the old custom-field storage for the fiscal day that was open
        /// when this device was imported. Cleared when the next fiscal day opens.
        /// </summary>
        public string? CarriedSummaryJson { get; set; }
        public int CarriedSummaryFiscalDayNo { get; set; }

        public string? LastCloseDayError { get; set; }

        /// <summary>ZIMRA money type (MoneyType value) used when an invoice does not say how it was paid.</summary>
        public int? DefaultMoneyType { get; set; }

        /// <summary>
        /// SHA-256 of the activation key the device was registered with. Lets the owner run setup again
        /// (for example after a failed save in Manager) without registering with ZIMRA a second time.
        /// </summary>
        public string? ActivationKeyHash { get; set; }

        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

        public List<FiscalReceipt> Receipts { get; set; } = [];
    }

    public static class ReceiptStatus
    {
        /// <summary>Numbers reserved and receipt signed, FDMS outcome not known yet.</summary>
        public const string Pending = "PENDING";
        /// <summary>Recorded by FDMS without blocking errors.</summary>
        public const string Approved = "APPROVED";
        /// <summary>Recorded by FDMS but flagged with Red or Grey validation errors.</summary>
        public const string Rejected = "REJECTED";
    }

    /// <summary>Every receipt sent (or about to be sent) to FDMS, exactly as signed.</summary>
    public class FiscalReceipt
    {
        public int Id { get; set; }
        public int DeviceRowId { get; set; }
        public Device? Device { get; set; }

        /// <summary>Key of the Manager invoice or credit note this receipt was made from.</summary>
        public string FormKey { get; set; } = string.Empty;

        public int FiscalDayNo { get; set; }
        public int ReceiptCounter { get; set; }
        public int ReceiptGlobalNo { get; set; }
        public ReceiptType ReceiptType { get; set; }
        public string Currency { get; set; } = string.Empty;
        public string InvoiceNo { get; set; } = string.Empty;
        /// <summary>Receipt date as signed (Zimbabwe local time, yyyy-MM-ddTHH:mm:ss).</summary>
        public string ReceiptDate { get; set; } = string.Empty;
        public decimal Total { get; set; }

        /// <summary>The exact request body receipt that was signed.</summary>
        public string ReceiptJson { get; set; } = string.Empty;
        public string Hash { get; set; } = string.Empty;
        public string Signature { get; set; } = string.Empty;
        public string QrCode { get; set; } = string.Empty;
        public string VerificationCode { get; set; } = string.Empty;

        public string Status { get; set; } = ReceiptStatus.Pending;
        public long? FdmsReceiptId { get; set; }
        public string? ResponseJson { get; set; }

        // Reference to the original receipt, for credit and debit notes.
        public string? RefJson { get; set; }

        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime? CompletedUtc { get; set; }
    }

    public class FiscalDbContext : DbContext
    {
        public FiscalDbContext(DbContextOptions<FiscalDbContext> options) : base(options) { }

        public DbSet<Device> Devices => Set<Device>();
        public DbSet<FiscalReceipt> Receipts => Set<FiscalReceipt>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Device>(e =>
            {
                e.HasIndex(d => new { d.DeviceID, d.Environment }).IsUnique();
                e.Property(d => d.Environment).HasConversion<string>();
                e.Property(d => d.FiscalDayStatus).HasConversion<string>();
            });

            modelBuilder.Entity<FiscalReceipt>(e =>
            {
                e.HasOne(r => r.Device).WithMany(d => d.Receipts).HasForeignKey(r => r.DeviceRowId);
                e.HasIndex(r => new { r.DeviceRowId, r.FiscalDayNo, r.ReceiptCounter }).IsUnique();
                e.HasIndex(r => new { r.DeviceRowId, r.FormKey });
                e.Property(r => r.ReceiptType).HasConversion<string>();
            });
        }
    }
}
