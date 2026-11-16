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
        private readonly FiscalDbContext _db;

        public AccountController(BillingService billing, FiscalDbContext db)
        {
            _billing = billing;
            _db = db;
        }

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
            var (account, error) = await _billing.CreateAccountAsync(form.Email, form.Password, form.Name, form.Phone);
            if (account == null)
            {
                form.Error = error; form.Password = string.Empty;
                return View(form);
            }
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
                form.Error = error; form.Password = string.Empty;
                return View(form);
            }
            await SignInAsync(account);
            return Back(form.ReturnUrl);
        }

        [HttpPost("signout"), ValidateAntiForgeryToken]
        public async Task<IActionResult> SignOutNow()
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToAction("Index", "Home");
        }

        [Authorize, HttpGet("")]
        public async Task<IActionResult> Index(string? message)
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
            });
        }

        [Authorize, HttpPost("password"), ValidateAntiForgeryToken]
        public async Task<IActionResult> ChangePassword(string current, string replacement)
        {
            string? error = await _billing.ChangePasswordAsync(CurrentAccountId(User)!.Value, current, replacement);
            return RedirectToAction(nameof(Index), new { message = error ?? "Your password has been changed." });
        }
    }
}
