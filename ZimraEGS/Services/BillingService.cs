using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ZimraEGS.ApiClient.Models;
using ZimraEGS.Data;

namespace ZimraEGS.Services
{
    public class BillingOptions
    {
        /// <summary>Off: every device may send, as before accounts existed.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Days a new live device may send before the first payment.</summary>
        public int TrialDays { get; set; } = 14;

        /// <summary>Days after the paid period during which sending continues, with a warning.</summary>
        public int GraceDays { get; set; } = 7;

        /// <summary>Warn this many days before the paid period ends.</summary>
        public int WarnDays { get; set; } = 7;

        /// <summary>E-mail addresses of the accounts that may use the admin page.</summary>
        public string[] AdminEmails { get; set; } = [];

        /// <summary>Shown to customers: how to pay. Payments are recorded by hand on the admin page.</summary>
        public string PaymentInstructions { get; set; } = "To renew, contact us. Your subscription is extended as soon as the payment is received.";

        public string Price { get; set; } = string.Empty;
    }

    public enum BillingState
    {
        /// <summary>Test environment, or billing switched off.</summary>
        Free,
        Active,
        /// <summary>Paid up, but the period ends within WarnDays.</summary>
        EndingSoon,
        /// <summary>The period has ended; sending continues for GraceDays.</summary>
        Grace,
        /// <summary>New documents are not sent until a payment is recorded.</summary>
        Expired,
        /// <summary>A live device that belongs to no account yet.</summary>
        NoAccount,
    }

    public record BillingStatus(BillingState State, DateTime? PaidUntilUtc, int? DaysLeft)
    {
        /// <summary>New documents may be sent to ZIMRA.</summary>
        public bool MaySend => State is BillingState.Free or BillingState.Active or BillingState.EndingSoon or BillingState.Grace;

        public string Describe(BillingOptions options)
        {
            string until = PaidUntilUtc is { } d ? d.AddHours(2).ToString("d MMMM yyyy") : string.Empty;
            return State switch
            {
                BillingState.Free => "Test environment: free.",
                BillingState.Active => $"Paid until {until}.",
                BillingState.EndingSoon => $"Paid until {until}: {DaysLeft} day{(DaysLeft == 1 ? "" : "s")} left.",
                BillingState.Grace => $"The subscription ended on {until}. Sending continues for {DaysLeft} more day{(DaysLeft == 1 ? "" : "s")}.",
                BillingState.Expired => $"The subscription ended on {until}. New documents are not sent to ZIMRA until it is renewed. Closing the fiscal day and printing fiscalised documents still work.",
                BillingState.NoAccount => "This live device is not linked to an account. New documents are not sent until it is.",
                _ => string.Empty,
            };
        }
    }

    /// <summary>Accounts, the paid period of each device, and payments recorded by an administrator.</summary>
    public class BillingService
    {
        private readonly FiscalDbContext _db;
        private readonly BillingOptions _options;
        private readonly PasswordHasher<Account> _hasher = new();

        public BillingService(FiscalDbContext db, Microsoft.Extensions.Options.IOptions<BillingOptions> options)
        {
            _db = db;
            _options = options.Value;
        }

        public BillingOptions Options => _options;

        public static string NormaliseEmail(string? email) => (email ?? string.Empty).Trim().ToLowerInvariant();

        public bool IsAdmin(string? email) =>
            _options.AdminEmails.Any(a => string.Equals(NormaliseEmail(a), NormaliseEmail(email), StringComparison.Ordinal));

        // ---- status

        public BillingStatus StatusOf(Device device, DateTime? nowUtc = null)
        {
            var now = nowUtc ?? DateTime.UtcNow;
            if (!_options.Enabled || device.Environment != PlatformType.Production)
            {
                return new BillingStatus(BillingState.Free, device.PaidUntilUtc, null);
            }
            if (device.AccountId == null)
            {
                return new BillingStatus(BillingState.NoAccount, device.PaidUntilUtc, null);
            }
            if (device.PaidUntilUtc is not { } until)
            {
                return new BillingStatus(BillingState.Expired, null, null);
            }

            if (now <= until)
            {
                int left = (int)Math.Ceiling((until - now).TotalDays);
                return new BillingStatus(left <= _options.WarnDays ? BillingState.EndingSoon : BillingState.Active, until, left);
            }

            var graceEnd = until.AddDays(_options.GraceDays);
            if (now <= graceEnd)
            {
                return new BillingStatus(BillingState.Grace, until, (int)Math.Ceiling((graceEnd - now).TotalDays));
            }
            return new BillingStatus(BillingState.Expired, until, 0);
        }

        // ---- accounts

        public const int MinimumPasswordLength = 10;

        public async Task<(Account? account, string? error)> CreateAccountAsync(string email, string password, string name, string phone)
        {
            email = NormaliseEmail(email);
            if (!System.Text.RegularExpressions.Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$")) return (null, "Enter a valid e-mail address.");
            if ((password ?? string.Empty).Length < MinimumPasswordLength) return (null, $"The password needs at least {MinimumPasswordLength} characters.");
            if (string.IsNullOrWhiteSpace(name)) return (null, "Enter the business name.");
            if (await _db.Accounts.AnyAsync(a => a.Email == email)) return (null, "There is already an account with this e-mail address. Sign in instead.");

            var account = new Account { Email = email, Name = name.Trim(), Phone = (phone ?? string.Empty).Trim() };
            account.PasswordHash = _hasher.HashPassword(account, password!);
            _db.Accounts.Add(account);
            await _db.SaveChangesAsync();
            return (account, null);
        }

        /// <summary>Checks e-mail and password. Five wrong passwords lock the account for 15 minutes.</summary>
        public async Task<(Account? account, string? error)> AuthenticateAsync(string email, string password)
        {
            email = NormaliseEmail(email);
            var account = await _db.Accounts.FirstOrDefaultAsync(a => a.Email == email);
            const string wrong = "The e-mail address or password is not right.";
            if (account == null)
            {
                _hasher.HashPassword(new Account(), password ?? string.Empty);   // same time taken as for a real account
                return (null, wrong);
            }
            if (account.LockedUntilUtc is { } locked && locked > DateTime.UtcNow)
            {
                return (null, "Too many wrong passwords. Try again in a few minutes.");
            }

            var result = _hasher.VerifyHashedPassword(account, account.PasswordHash, password ?? string.Empty);
            if (result == PasswordVerificationResult.Failed)
            {
                account.FailedSignIns++;
                if (account.FailedSignIns >= 5)
                {
                    account.LockedUntilUtc = DateTime.UtcNow.AddMinutes(15);
                    account.FailedSignIns = 0;
                }
                await _db.SaveChangesAsync();
                return (null, wrong);
            }

            if (result == PasswordVerificationResult.SuccessRehashNeeded) account.PasswordHash = _hasher.HashPassword(account, password!);
            account.FailedSignIns = 0;
            account.LockedUntilUtc = null;
            account.LastSignInUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return (account, null);
        }

        public async Task<string?> ChangePasswordAsync(int accountId, string current, string replacement)
        {
            var account = await _db.Accounts.FirstAsync(a => a.Id == accountId);
            if (_hasher.VerifyHashedPassword(account, account.PasswordHash, current ?? string.Empty) == PasswordVerificationResult.Failed)
                return "The current password is not right.";
            if ((replacement ?? string.Empty).Length < MinimumPasswordLength) return $"The new password needs at least {MinimumPasswordLength} characters.";
            account.PasswordHash = _hasher.HashPassword(account, replacement!);
            await _db.SaveChangesAsync();
            return null;
        }

        /// <summary>Sets a new password for a customer who forgot theirs. Admin page only.</summary>
        public async Task<string> ResetPasswordAsync(int accountId)
        {
            var account = await _db.Accounts.FirstAsync(a => a.Id == accountId);
            string password = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12)).Replace('+', 'x').Replace('/', 'y');
            account.PasswordHash = _hasher.HashPassword(account, password);
            account.FailedSignIns = 0;
            account.LockedUntilUtc = null;
            await _db.SaveChangesAsync();
            return password;
        }

        // ---- devices

        /// <summary>Links a device to an account. A live device gets its trial period the first time.</summary>
        public async Task LinkDeviceAsync(Device device, Account account)
        {
            device.AccountId = account.Id;
            if (device.Environment == PlatformType.Production && device.PaidUntilUtc == null && _options.TrialDays > 0)
            {
                device.PaidUntilUtc = DateTime.UtcNow.AddDays(_options.TrialDays);
            }
            device.UpdatedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        /// <summary>
        /// Records a payment and extends the paid period: from its current end while it is still running, otherwise
        /// from today, so time without a subscription is not charged for.
        /// </summary>
        public async Task<SubscriptionPayment> RecordPaymentAsync(int deviceRowId, int months, int extraDays, decimal? amount,
            string currency, string reference, string note, string recordedBy, DateTime? nowUtc = null)
        {
            if (months < 0 || extraDays < 0 || months + extraDays == 0) throw new ArgumentException("Enter the number of months or days paid for.");
            if (months > 36 || extraDays > 366) throw new ArgumentException("That period is unusually long. Check the number of months or days.");

            var now = nowUtc ?? DateTime.UtcNow;
            var device = await _db.Devices.FirstAsync(d => d.Id == deviceRowId);
            var from = device.PaidUntilUtc is { } until && until > now ? until : now;
            var to = from.AddMonths(months).AddDays(extraDays);

            var payment = new SubscriptionPayment
            {
                DeviceRowId = device.Id, Months = months, ExtraDays = extraDays, Amount = amount,
                Currency = (currency ?? string.Empty).Trim().ToUpperInvariant(), Reference = (reference ?? string.Empty).Trim(),
                Note = (note ?? string.Empty).Trim(), PeriodFromUtc = from, PeriodToUtc = to, RecordedBy = recordedBy, RecordedUtc = now,
            };
            device.PaidUntilUtc = to;
            device.UpdatedUtc = now;
            _db.SubscriptionPayments.Add(payment);
            await _db.SaveChangesAsync();
            return payment;
        }
    }
}
