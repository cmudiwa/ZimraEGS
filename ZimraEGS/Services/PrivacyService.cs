using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ZimraEGS.Data;

namespace ZimraEGS.Services
{
    /// <summary>
    /// Who runs this service and how personal data is handled, for the privacy notice required by the
    /// Cyber and Data Protection Act [Chapter 12:07] and its regulations (SI 155 of 2024), administered by POTRAZ.
    /// </summary>
    public class PrivacyOptions
    {
        /// <summary>The data controller: your business's legal name.</summary>
        public string ControllerName { get; set; } = string.Empty;
        public string ControllerAddress { get; set; } = string.Empty;
        public string ContactEmail { get; set; } = string.Empty;
        public string ContactPhone { get; set; } = string.Empty;

        /// <summary>The data protection officer notified to POTRAZ (Form DP2).</summary>
        public string DpoName { get; set; } = string.Empty;
        public string DpoEmail { get; set; } = string.Empty;

        /// <summary>Data controller licence number issued by POTRAZ.</summary>
        public string PotrazLicenceNumber { get; set; } = string.Empty;

        /// <summary>Where the server and its database are, e.g. "Harare, Zimbabwe".</summary>
        public string HostingLocation { get; set; } = string.Empty;

        /// <summary>Changes whenever the notice changes in substance; customers then agree again.</summary>
        public string NoticeVersion { get; set; } = "2026-11-16";

        /// <summary>Fiscal documents and payments are kept this long, as tax law requires.</summary>
        public int RecordsRetentionYears { get; set; } = 6;

        /// <summary>Security log entries are deleted after this many days.</summary>
        public int AuditRetentionDays { get; set; } = 365;

        /// <summary>Server log files are deleted after this many days.</summary>
        public int LogRetentionDays { get; set; } = 90;

        /// <summary>Uploaded Excel workbooks are deleted after this many days. The fiscal documents are kept.</summary>
        public int ExcelRetentionDays { get; set; } = 30;
    }

    public class PrivacyService
    {
        private readonly FiscalDbContext _db;
        private readonly PrivacyOptions _options;

        public PrivacyService(FiscalDbContext db, IOptions<PrivacyOptions> options)
        {
            _db = db;
            _options = options.Value;
        }

        public PrivacyOptions Options => _options;

        /// <summary>Settings a privacy notice needs that are still empty, by what they are called on the page.</summary>
        public List<string> MissingSettings()
        {
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(_options.ControllerName)) missing.Add("Privacy:ControllerName (your business's legal name)");
            if (string.IsNullOrWhiteSpace(_options.ControllerAddress)) missing.Add("Privacy:ControllerAddress");
            if (string.IsNullOrWhiteSpace(_options.ContactEmail)) missing.Add("Privacy:ContactEmail");
            if (string.IsNullOrWhiteSpace(_options.DpoName) || string.IsNullOrWhiteSpace(_options.DpoEmail)) missing.Add("Privacy:DpoName and Privacy:DpoEmail (data protection officer)");
            if (string.IsNullOrWhiteSpace(_options.PotrazLicenceNumber)) missing.Add("Privacy:PotrazLicenceNumber");
            if (string.IsNullOrWhiteSpace(_options.HostingLocation)) missing.Add("Privacy:HostingLocation (where the server is)");
            return missing;
        }

        public async Task LogAsync(string actor, string action, int? accountId = null, string detail = "", string? ip = null)
        {
            _db.AuditEvents.Add(new AuditEvent
            {
                Actor = Trim(actor, 200), Action = Trim(action, 100), AccountId = accountId,
                Detail = Trim(detail, 1000), IpAddress = Trim(ip ?? string.Empty, 64),
            });
            await _db.SaveChangesAsync();
        }

        private static string Trim(string value, int max) => value.Length <= max ? value : value[..max];

        /// <summary>Everything held about one customer, as a JSON document they can keep (right of access).</summary>
        public async Task<string> ExportAsync(int accountId)
        {
            var account = await _db.Accounts.AsNoTracking().Include(a => a.Devices).FirstAsync(a => a.Id == accountId);
            var deviceIds = account.Devices.Select(d => d.Id).ToList();
            var payments = await _db.SubscriptionPayments.AsNoTracking().Where(p => deviceIds.Contains(p.DeviceRowId)).OrderBy(p => p.RecordedUtc).ToListAsync();
            var receipts = await _db.Receipts.AsNoTracking().Where(r => deviceIds.Contains(r.DeviceRowId)).OrderBy(r => r.DeviceRowId).ThenBy(r => r.ReceiptGlobalNo).ToListAsync();
            var events = await _db.AuditEvents.AsNoTracking().Where(e => e.AccountId == accountId).OrderBy(e => e.Utc).ToListAsync();

            var doc = new JObject
            {
                ["exported"] = DateTime.UtcNow,
                ["controller"] = _options.ControllerName,
                ["account"] = new JObject
                {
                    ["email"] = account.Email, ["name"] = account.Name, ["phone"] = account.Phone,
                    ["created"] = account.CreatedUtc, ["lastSignIn"] = account.LastSignInUtc,
                    ["privacyNoticeAgreed"] = account.ConsentVersion, ["agreedOn"] = account.ConsentUtc,
                    ["password"] = "stored only as a one-way hash; not included",
                },
                ["devices"] = new JArray(account.Devices.OrderBy(d => d.DeviceID).Select(d => new JObject
                {
                    ["deviceId"] = d.DeviceID, ["environment"] = d.Environment.ToString(), ["serialNumber"] = d.SerialNumber,
                    ["taxpayer"] = JObject.Parse(string.IsNullOrEmpty(d.InfoJson) ? "{}" : d.InfoJson),
                    ["paidUntil"] = d.PaidUntilUtc, ["fiscalDay"] = d.FiscalDayNo, ["registered"] = d.CreatedUtc,
                    ["privateKey"] = "held encrypted for signing receipts; not included",
                })),
                ["payments"] = JArray.FromObject(payments.Select(p => new
                {
                    recorded = p.RecordedUtc, months = p.Months, days = p.ExtraDays, amount = p.Amount, currency = p.Currency,
                    reference = p.Reference, note = p.Note, from = p.PeriodFromUtc, to = p.PeriodToUtc,
                })),
                ["fiscalDocuments"] = new JArray(receipts.Select(r => new JObject
                {
                    ["deviceId"] = account.Devices.First(d => d.Id == r.DeviceRowId).DeviceID,
                    ["invoiceNo"] = r.InvoiceNo, ["type"] = r.ReceiptType.ToString(), ["date"] = r.ReceiptDate,
                    ["currency"] = r.Currency, ["total"] = r.Total, ["status"] = r.Status,
                    ["verificationCode"] = r.VerificationCode,
                    ["receipt"] = JToken.Parse(string.IsNullOrEmpty(r.ReceiptJson) ? "{}" : r.ReceiptJson),
                })),
                ["securityLog"] = JArray.FromObject(events.Select(e => new { e.Utc, e.Actor, e.Action, e.Detail, e.IpAddress })),
            };
            return doc.ToString(Formatting.Indented);
        }

        /// <summary>
        /// Deletes a customer account (right to erasure). Devices are unlinked, not deleted: their fiscal documents
        /// and the payments made for them are records the law requires to be kept. The customer's e-mail is removed
        /// from the security log.
        /// </summary>
        public async Task DeleteAccountAsync(int accountId, string actor, string? ip)
        {
            var account = await _db.Accounts.Include(a => a.Devices).FirstAsync(a => a.Id == accountId);
            string email = account.Email;
            int devices = account.Devices.Count;
            foreach (var d in account.Devices) { d.AccountId = null; d.UpdatedUtc = DateTime.UtcNow; }
            _db.Accounts.Remove(account);
            await _db.ExcelBatches.Where(b => b.AccountId == accountId).ExecuteDeleteAsync();

            string replacement = $"deleted account {accountId}";
            await _db.AuditEvents.Where(e => e.Actor == email).ExecuteUpdateAsync(u => u.SetProperty(e => e.Actor, replacement));
            await _db.SaveChangesAsync();
            await LogAsync(actor == email ? replacement : actor, "account deleted", accountId, $"{devices} device(s) unlinked", ip);
        }

        /// <summary>Deletes security log entries and server log files past their retention period.</summary>
        public async Task<int> PurgeAsync(string? logDirectory, DateTime? nowUtc = null)
        {
            var now = nowUtc ?? DateTime.UtcNow;
            var auditCutoff = now.AddDays(-Math.Max(1, _options.AuditRetentionDays));
            int removed = await _db.AuditEvents.Where(e => e.Utc < auditCutoff).ExecuteDeleteAsync();
            var excelCutoff = now.AddDays(-Math.Max(1, _options.ExcelRetentionDays));
            removed += await _db.ExcelBatches.Where(b => b.CreatedUtc < excelCutoff).ExecuteDeleteAsync();

            if (!string.IsNullOrEmpty(logDirectory) && Directory.Exists(logDirectory))
            {
                var logCutoff = now.AddDays(-Math.Max(1, _options.LogRetentionDays));
                foreach (var file in Directory.EnumerateFiles(logDirectory, "log-*.log"))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(file) < logCutoff) { File.Delete(file); removed++; }
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            return removed;
        }
    }

    /// <summary>Applies the retention periods once a day.</summary>
    public class PrivacyMaintenanceService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly ILogger<PrivacyMaintenanceService> _logger;
        private readonly string _logDirectory;

        public PrivacyMaintenanceService(IServiceScopeFactory scopes, ILogger<PrivacyMaintenanceService> logger, LogDirectory logDirectory)
        {
            _scopes = scopes;
            _logger = logger;
            _logDirectory = logDirectory.Path;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopes.CreateScope();
                    int removed = await scope.ServiceProvider.GetRequiredService<PrivacyService>().PurgeAsync(_logDirectory);
                    if (removed > 0) _logger.LogInformation("Retention: removed {Count} expired log entries and files.", removed);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Retention clean-up failed; it is tried again tomorrow.");
                }
                try { await Task.Delay(TimeSpan.FromHours(24), stoppingToken); } catch (TaskCanceledException) { }
            }
        }
    }

    public record LogDirectory(string Path);
}
