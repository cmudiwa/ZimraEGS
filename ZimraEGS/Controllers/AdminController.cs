using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Security.Claims;
using ZimraEGS.Data;
using ZimraEGS.Models;
using ZimraEGS.Services;

namespace ZimraEGS.Controllers
{
    /// <summary>For the operator of the service: customers, devices and payments recorded by hand.</summary>
    [Authorize(Policy = "Admin"), Route("admin")]
    public class AdminController : Controller
    {
        private readonly BillingService _billing;
        private readonly PrivacyService _privacy;
        private readonly FiscalDbContext _db;
        private readonly IConfiguration _config;
        private readonly AutoCloseDayOptions _autoClose;

        public AdminController(BillingService billing, PrivacyService privacy, FiscalDbContext db, IConfiguration config,
            Microsoft.Extensions.Options.IOptions<AutoCloseDayOptions> autoClose)
        {
            _autoClose = autoClose.Value;
            _billing = billing;
            _privacy = privacy;
            _db = db;
            _config = config;
        }

        private string AdminEmail => User.FindFirstValue(ClaimTypes.Email) ?? "admin";
        private string? Ip => HttpContext.Connection.RemoteIpAddress?.ToString();

        [HttpGet("")]
        public async Task<IActionResult> Index(string filter = "attention", string? message = null)
        {
            var devices = await _db.Devices.AsNoTracking().Include(d => d.Account).Where(d => d.IsActive).OrderBy(d => d.DeviceID).ToListAsync();
            var withStatus = devices.Select(d => (d, _billing.StatusOf(d))).ToList();

            var shown = filter switch
            {
                "live" => withStatus.Where(x => x.d.Environment == ZimraEGS.ApiClient.Models.PlatformType.Production).ToList(),
                "all" => withStatus,
                _ => withStatus.Where(x => x.Item2.State is BillingState.EndingSoon or BillingState.Grace or BillingState.Expired or BillingState.NoAccount).ToList(),
            };

            return View(new AdminPageModel
            {
                Accounts = await _db.Accounts.AsNoTracking().Include(a => a.Devices).OrderBy(a => a.Name).ToListAsync(),
                Devices = shown,
                Filter = filter,
                Message = message,
                Server = new HomeViewModel
                {
                    TestDevices = devices.Count(d => d.Environment == ZimraEGS.ApiClient.Models.PlatformType.Simulation),
                    LiveDevices = devices.Count(d => d.Environment == ZimraEGS.ApiClient.Models.PlatformType.Production),
                    OpenFiscalDays = devices.Count(d => d.FiscalDayStatus != ZimraEGS.ApiClient.Enums.FiscalDayStatus.FiscalDayClosed),
                    AutoCloseEnabled = _autoClose.Enabled,
                    AutoCloseTime = _autoClose.TimeOfDay,
                },
            });
        }

        private async Task<AdminDeviceModel?> DeviceModelAsync(int id)
        {
            var device = await _db.Devices.AsNoTracking().Include(d => d.Account).FirstOrDefaultAsync(d => d.Id == id);
            if (device == null) return null;
            return new AdminDeviceModel
            {
                Device = device,
                Status = _billing.StatusOf(device),
                Payments = await _db.SubscriptionPayments.AsNoTracking().Where(p => p.DeviceRowId == id).OrderByDescending(p => p.RecordedUtc).ToListAsync(),
                Accounts = await _db.Accounts.AsNoTracking().OrderBy(a => a.Name).ToListAsync(),
                TaxpayerName = DeviceStore.GetInfo(device).TaxPayerName ?? string.Empty,
                Options = _billing.Options,
            };
        }

        [HttpGet("device/{id:int}")]
        public async Task<IActionResult> Device(int id, string? message)
        {
            var model = await DeviceModelAsync(id);
            if (model == null) return NotFound();
            model.Message = message;
            return View(model);
        }

        /// <summary>Records a payment received outside the system and extends the device's paid period.</summary>
        [HttpPost("device/{id:int}/payment"), ValidateAntiForgeryToken]
        public async Task<IActionResult> RecordPayment(int id, int months, int extraDays, string? amount, string? currency, string? reference, string? note)
        {
            decimal? parsed = null;
            if (!string.IsNullOrWhiteSpace(amount))
            {
                if (!decimal.TryParse(amount.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var a) || a < 0)
                {
                    return await DeviceWithError(id, "Enter the amount as a number, for example 25.00.");
                }
                parsed = a;
            }

            try
            {
                var payment = await _billing.RecordPaymentAsync(id, months, extraDays, parsed, currency ?? "", reference ?? "", note ?? "", AdminEmail);
                var paid = await _db.Devices.AsNoTracking().FirstAsync(d => d.Id == id);
                await _privacy.LogAsync(AdminEmail, "payment recorded", paid.AccountId, $"device {paid.DeviceID}: {months} month(s) {extraDays} day(s), {payment.Currency} {payment.Amount}", Ip);
                return RedirectToAction(nameof(Device), new { id, message = $"Payment recorded. Paid until {payment.PeriodToUtc.AddHours(2):d MMMM yyyy}." });
            }
            catch (ArgumentException ex)
            {
                return await DeviceWithError(id, ex.Message);
            }
        }

        private async Task<IActionResult> DeviceWithError(int id, string error)
        {
            var model = await DeviceModelAsync(id);
            if (model == null) return NotFound();
            model.Error = error;
            return View(nameof(Device), model);
        }

        [HttpPost("device/{id:int}/account"), ValidateAntiForgeryToken]
        public async Task<IActionResult> LinkAccount(int id, int accountId)
        {
            var device = await _db.Devices.FirstOrDefaultAsync(d => d.Id == id);
            var account = await _db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId);
            if (device == null || account == null) return NotFound();
            await _billing.LinkDeviceAsync(device, account);
            await _privacy.LogAsync(AdminEmail, "device linked", account.Id, $"device {device.DeviceID}", Ip);
            return RedirectToAction(nameof(Device), new { id, message = $"Linked to {account.Name}." });
        }

        [HttpGet("account/{id:int}")]
        public async Task<IActionResult> Account(int id, string? message)
        {
            string? newPassword = TempData["NewPassword"] as string;
            var account = await _db.Accounts.AsNoTracking().Include(a => a.Devices).FirstOrDefaultAsync(a => a.Id == id);
            if (account == null) return NotFound();
            await _privacy.LogAsync(AdminEmail, "account viewed", id, "", Ip);
            return View(new AdminAccountModel
            {
                Account = account,
                Devices = account.Devices.OrderBy(d => d.DeviceID).Select(d => (d, _billing.StatusOf(d))).ToList(),
                NewPassword = newPassword,
                Message = message,
            });
        }

        /// <summary>A customer's data, for a request received by e-mail or letter.</summary>
        [HttpGet("account/{id:int}/export")]
        public async Task<IActionResult> ExportAccount(int id)
        {
            var account = await _db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id);
            if (account == null) return NotFound();
            await _privacy.LogAsync(AdminEmail, "data exported for customer", id, "", Ip);
            string json = await _privacy.ExportAsync(id);
            return File(System.Text.Encoding.UTF8.GetBytes(json), "application/json", $"customer-{id}-data-{DateTime.UtcNow:yyyy-MM-dd}.json");
        }

        /// <summary>Deletes a customer's account at their request.</summary>
        [HttpPost("account/{id:int}/delete"), ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteAccount(int id, string? confirm)
        {
            var account = await _db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id);
            if (account == null) return NotFound();
            if (!string.Equals((confirm ?? "").Trim(), "DELETE", StringComparison.Ordinal))
                return RedirectToAction(nameof(Account), new { id, message = "Type DELETE in capitals to confirm. Nothing was deleted." });
            await _privacy.DeleteAccountAsync(id, AdminEmail, Ip);
            return RedirectToAction(nameof(Index), new { filter = "all", message = $"The account of {account.Name} was deleted. Its devices are no longer linked to an account." });
        }

        /// <summary>Data protection: what is still to be set up, and the security log.</summary>
        [HttpGet("privacy")]
        public async Task<IActionResult> Privacy(string? q)
        {
            var events = _db.AuditEvents.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(q))
            {
                string term = q.Trim().ToLowerInvariant();
                events = events.Where(e => e.Actor.ToLower().Contains(term) || e.Action.ToLower().Contains(term) || e.Detail.ToLower().Contains(term) || e.IpAddress.Contains(term));
            }
            return View(new AdminPrivacyModel
            {
                Options = _privacy.Options,
                Missing = _privacy.MissingSettings(),
                HttpsOnly = _config.GetValue<bool>("Security:HttpsOnly"),
                IsHttps = Request.IsHttps,
                Events = await events.OrderByDescending(e => e.Utc).Take(300).ToListAsync(),
                Search = q,
            });
        }

        [HttpPost("account/{id:int}/reset-password"), ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetPassword(int id)
        {
            if (!await _db.Accounts.AnyAsync(a => a.Id == id)) return NotFound();
            string password = await _billing.ResetPasswordAsync(id);
            await _privacy.LogAsync(AdminEmail, "password reset by administrator", id, "", Ip);
            // Shown once, on this page only; give it to the customer and ask them to change it.
            TempData["NewPassword"] = password;
            return RedirectToAction(nameof(Account), new { id });
        }
    }
}
