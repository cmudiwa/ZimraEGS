using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ZimraEGS.Data;
using ZimraEGS.Models;
using ZimraEGS.Services;

namespace ZimraEGS.Controllers
{
    [Route("account")]
    public class AccountController : Controller
    {
        private readonly BillingService _billing;
        private readonly PrivacyService _privacy;
        private readonly FiscalDbContext _db;

        public AccountController(BillingService billing, PrivacyService privacy, FiscalDbContext db)
        {
            _billing = billing;
            _privacy = privacy;
            _db = db;
        }

        private string? Ip => HttpContext.Connection.RemoteIpAddress?.ToString();
        private string Me => User.FindFirstValue(ClaimTypes.Email) ?? "unknown";

        public static int? CurrentAccountId(ClaimsPrincipal user) =>
            int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out int id) ? id : null;

        private async Task SignInAsync(Account account)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, account.Id.ToString()),
                new(ClaimTypes.Email, account.Email),
                new(ClaimTypes.Name, account.Name),
            };
            if (_billing.IsAdmin(account.Email)) claims.Add(new Claim("zimraegs:admin", "true"));
            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        }

        private IActionResult Back(string? returnUrl) =>
            !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? Redirect(returnUrl) : RedirectToAction(nameof(Index));

        [HttpGet("signup")]
        public IActionResult SignUp() => View(new AccountFormModel());

        [HttpPost("signup"), ValidateAntiForgeryToken]
        public async Task<IActionResult> SignUp(AccountFormModel form)
        {
            if (!form.Agree)
            {
                form.Error = "Read the privacy notice and tick the box to agree to it.";
                form.Password = string.Empty;
                return View(form);
            }
            var (account, error) = await _billing.CreateAccountAsync(form.Email, form.Password, form.Name, form.Phone, _privacy.Options.NoticeVersion);
            if (account == null)
            {
                form.Error = error; form.Password = string.Empty;
                return View(form);
            }
            await _privacy.LogAsync(account.Email, "account created", account.Id, $"agreed to privacy notice {account.ConsentVersion}", Ip);
            await SignInAsync(account);
            return RedirectToAction(nameof(Index));
        }

        [HttpGet("signin")]
        public IActionResult SignIn(string? returnUrl) => View(new AccountFormModel { ReturnUrl = returnUrl });

        [HttpPost("signin"), ValidateAntiForgeryToken]
        public async Task<IActionResult> SignIn(AccountFormModel form)
        {
            var (account, error) = await _billing.AuthenticateAsync(form.Email, form.Password);
            if (account == null)
            {
                var known = await _db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Email == BillingService.NormaliseEmail(form.Email));
                await _privacy.LogAsync(known?.Email ?? "anonymous", "sign-in refused", known?.Id, error ?? "", Ip);
                form.Error = error; form.Password = string.Empty;
                return View(form);
            }
            await _privacy.LogAsync(account.Email, "signed in", account.Id, "", Ip);
            await SignInAsync(account);
            return Back(form.ReturnUrl);
        }

        [HttpPost("signout"), ValidateAntiForgeryToken]
        public async Task<IActionResult> SignOutNow()
        {
            if (User.Identity?.IsAuthenticated == true) await _privacy.LogAsync(Me, "signed out", CurrentAccountId(User), "", Ip);
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToAction("Index", "Home");
        }

        [Authorize, HttpGet("")]
        public async Task<IActionResult> Index(string? message, string? error)
        {
            int id = CurrentAccountId(User)!.Value;
            var account = await _db.Accounts.AsNoTracking().Include(a => a.Devices).FirstOrDefaultAsync(a => a.Id == id);
            if (account == null)
            {
                await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return RedirectToAction(nameof(SignIn));
            }

            var deviceIds = account.Devices.Select(d => d.Id).ToList();
            var payments = await _db.SubscriptionPayments.AsNoTracking().Where(p => deviceIds.Contains(p.DeviceRowId))
                .OrderByDescending(p => p.RecordedUtc).ToListAsync();

            return View(new AccountPageModel
            {
                Account = account,
                Devices = account.Devices.OrderBy(d => d.DeviceID).Select(d => (d, _billing.StatusOf(d))).ToList(),
                Payments = payments,
                Options = _billing.Options,
                IsAdmin = _billing.IsAdmin(account.Email),
                Message = message,
                Error = error,
                NeedsConsent = account.ConsentVersion != _privacy.Options.NoticeVersion,
            });
        }

        /// <summary>Agrees to the current privacy notice, after it changed.</summary>
        [Authorize, HttpPost("consent"), ValidateAntiForgeryToken]
        public async Task<IActionResult> Consent()
        {
            var account = await _db.Accounts.FirstAsync(a => a.Id == CurrentAccountId(User)!.Value);
            account.ConsentVersion = _privacy.Options.NoticeVersion;
            account.ConsentUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            await _privacy.LogAsync(account.Email, "agreed to privacy notice", account.Id, account.ConsentVersion, Ip);
            return RedirectToAction(nameof(Index), new { message = "Thank you. Your agreement has been recorded." });
        }

        /// <summary>Corrects the customer's name and phone (right to correction).</summary>
        [Authorize, HttpPost("details"), ValidateAntiForgeryToken]
        public async Task<IActionResult> Details(string name, string? phone)
        {
            if (string.IsNullOrWhiteSpace(name)) return RedirectToAction(nameof(Index), new { error = "Enter the business name." });
            var account = await _db.Accounts.FirstAsync(a => a.Id == CurrentAccountId(User)!.Value);
            account.Name = name.Trim();
            account.Phone = (phone ?? string.Empty).Trim();
            await _db.SaveChangesAsync();
            await _privacy.LogAsync(account.Email, "details changed", account.Id, "", Ip);
            await SignInAsync(account);
            return RedirectToAction(nameof(Index), new { message = "Your details have been saved." });
        }

        /// <summary>Downloads everything held about the customer (right of access).</summary>
        [Authorize, HttpGet("export")]
        public async Task<IActionResult> Export()
        {
            int id = CurrentAccountId(User)!.Value;
            await _privacy.LogAsync(Me, "data downloaded", id, "", Ip);
            string json = await _privacy.ExportAsync(id);
            return File(System.Text.Encoding.UTF8.GetBytes(json), "application/json", $"my-data-{DateTime.UtcNow:yyyy-MM-dd}.json");
        }

        /// <summary>Deletes the account (right to erasure). Needs the password.</summary>
        [Authorize, HttpPost("delete"), ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(string password, string? confirm)
        {
            int id = CurrentAccountId(User)!.Value;
            var account = await _db.Accounts.AsNoTracking().FirstAsync(a => a.Id == id);
            if (!string.Equals((confirm ?? "").Trim(), "DELETE", StringComparison.Ordinal))
                return RedirectToAction(nameof(Index), new { error = "Type DELETE in capitals to confirm." });
            var (check, _) = await _billing.AuthenticateAsync(account.Email, password);
            if (check == null) return RedirectToAction(nameof(Index), new { error = "The password is not right. The account was not deleted." });

            await _privacy.DeleteAccountAsync(id, account.Email, Ip);
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToAction("Index", "Privacy", new { deleted = true });
        }

        [Authorize, HttpPost("password"), ValidateAntiForgeryToken]
        public async Task<IActionResult> ChangePassword(string current, string replacement)
        {
            int id = CurrentAccountId(User)!.Value;
            string? error = await _billing.ChangePasswordAsync(id, current, replacement);
            if (error != null) return RedirectToAction(nameof(Index), new { error });
            await _privacy.LogAsync(Me, "password changed", id, "", Ip);
            return RedirectToAction(nameof(Index), new { message = "Your password has been changed." });
        }
    }
}
