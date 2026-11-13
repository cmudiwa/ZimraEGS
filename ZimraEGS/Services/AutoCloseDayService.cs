using Microsoft.EntityFrameworkCore;
using System.Globalization;
using ZimraEGS.ApiClient.Enums;
using ZimraEGS.Data;
using ZimraEGS.Helpers;

namespace ZimraEGS.Services
{
    public class AutoCloseDayOptions
    {
        public bool Enabled { get; set; } = true;

        /// <summary>Zimbabwe local time (HH:mm) after which open fiscal days are closed.</summary>
        public string TimeOfDay { get; set; } = "23:30";

        public int CheckIntervalMinutes { get; set; } = 10;

        /// <summary>Also close a day this many hours before it reaches the maximum length ZIMRA allows.</summary>
        public int HoursBeforeLimit { get; set; } = 1;
    }

    /// <summary>
    /// Closes open fiscal days without anyone having to remember: once a day at the configured time,
    /// and in any case before a day reaches ZIMRA's maximum length.
    /// </summary>
    public class AutoCloseDayService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly AutoCloseDayOptions _options;
        private readonly ILogger<AutoCloseDayService> _logger;

        public AutoCloseDayService(IServiceScopeFactory scopeFactory, Microsoft.Extensions.Options.IOptions<AutoCloseDayOptions> options, ILogger<AutoCloseDayService> logger)
        {
            _scopeFactory = scopeFactory;
            _options = options.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_options.Enabled) return;

            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, _options.CheckIntervalMinutes)));
            try
            {
                while (await timer.WaitForNextTickAsync(stoppingToken))
                {
                    try
                    {
                        await RunOnceAsync(TimeZoneHelper.GetSouthAfricaTime());
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Automatic day close run failed");
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        /// <summary>True when a day opened at <paramref name="opened"/> is due for closing at <paramref name="now"/>.</summary>
        public static bool IsDue(DateTimeOffset opened, DateTimeOffset now, AutoCloseDayOptions options, int maxDayHours)
        {
            if (maxDayHours > 0 && now >= opened.AddHours(maxDayHours - options.HoursBeforeLimit))
            {
                return true;
            }

            if (!TimeSpan.TryParseExact(options.TimeOfDay, @"hh\:mm", CultureInfo.InvariantCulture, out var closeTime))
            {
                return false;
            }

            // The most recent closing time that has already passed.
            var cutoff = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset).Add(closeTime);
            if (cutoff > now) cutoff = cutoff.AddDays(-1);

            return opened < cutoff;
        }

        public async Task RunOnceAsync(DateTimeOffset now)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FiscalDbContext>();
            var service = scope.ServiceProvider.GetRequiredService<FiscalService>();

            var devices = await db.Devices.AsNoTracking()
                // A day whose close failed needs someone to look at it; it is not retried on every tick.
                .Where(d => d.IsActive && d.FiscalDayOpened != null &&
                            (d.FiscalDayStatus == FiscalDayStatus.FiscalDayOpened || d.FiscalDayStatus == FiscalDayStatus.FiscalDayCloseInitiated))
                .ToListAsync();

            foreach (var device in devices)
            {
                var opened = TimeZoneHelper.ParseFdmsString(device.FiscalDayOpened);
                if (opened == null) continue;

                int maxHours = DeviceStore.GetConfig(device)?.TaxPayerDayMaxHrs ?? DeviceStore.GetInfo(device).TaxPayerDayMaxHrs;
                if (!IsDue(opened.Value, now.ToOffset(TimeZoneHelper.ZimbabweOffset), _options, maxHours)) continue;

                var result = await service.CloseDayAsync(device.Id);
                if (result.Success)
                {
                    _logger.LogInformation("Automatically closed fiscal day {DayNo} of device {DeviceID}", device.FiscalDayNo, device.DeviceID);
                }
                else
                {
                    _logger.LogWarning("Automatic close of fiscal day {DayNo} of device {DeviceID} failed: {Message}", device.FiscalDayNo, device.DeviceID, result.Message);
                }
            }
        }
    }
}
