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
        private readonly FiscalDbContext _db;

        public AdminController(BillingService billing, FiscalDbContext db)
        {
            _billing = billing;
            _db = db;
        }

        private string AdminEmail => User.FindFirstValue(ClaimTypes.Email) ?? "admin";

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
            return RedirectToAction(nameof(Device), new { id, message = $"Linked to {account.Name}." });
        }

        [HttpGet("account/{id:int}")]
        public async Task<IActionResult> Account(int id)
        {
            string? newPassword = TempData["NewPassword"] as string;
            var account = await _db.Accounts.AsNoTracking().Include(a => a.Devices).FirstOrDefaultAsync(a => a.Id == id);
            if (account == null) return NotFound();
            return View(new AdminAccountModel
            {
                Account = account,
                Devices = account.Devices.OrderBy(d => d.DeviceID).Select(d => (d, _billing.StatusOf(d))).ToList(),
                NewPassword = newPassword,
            });
        }

        [HttpPost("account/{id:int}/reset-password"), ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetPassword(int id)
        {
            if (!await _db.Accounts.AnyAsync(a => a.Id == id)) return NotFound();
            string password = await _billing.ResetPasswordAsync(id);
            // Shown once, on this page only; give it to the customer and ask them to change it.
            TempData["NewPassword"] = password;
            return RedirectToAction(nameof(Account), new { id });
        }
    }
}
