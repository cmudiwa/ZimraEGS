using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using ZimraEGS.ApiClient.Enums;
using ZimraEGS.ApiClient.Models;
using ZimraEGS.Data;
using ZimraEGS.Helpers;
using ZimraEGS.Models;
using ZimraEGS.Services;

namespace ZimraEGS.Controllers
{
    public class HomeController : Controller
    {
        private readonly ILogger<HomeController> _logger;
        private readonly FiscalDbContext _db;
        private readonly AutoCloseDayOptions _autoClose;
        private readonly HsCodeCatalog _hsCodes;

        public HomeController(ILogger<HomeController> logger, FiscalDbContext db, IOptions<AutoCloseDayOptions> autoClose, HsCodeCatalog hsCodes)
        {
            _logger = logger;
            _db = db;
            _autoClose = autoClose.Value;
            _hsCodes = hsCodes;
        }

        public async Task<IActionResult> Index()
        {
            var model = new HomeViewModel
            {
                Version = VersionHelper.GetVersion(),
                ExtensionEndpoint = $"{Request.Scheme}://{Request.Host}{Request.PathBase}/extension",
                IsHttps = Request.IsHttps,
                IsLocal = Request.Host.Host is "localhost" or "127.0.0.1" or "::1",
                AutoCloseEnabled = _autoClose.Enabled,
                AutoCloseTime = _autoClose.TimeOfDay,
                HsCodeCount = _hsCodes.Count,
            };

            try
            {
                var devices = await _db.Devices.AsNoTracking().Where(d => d.IsActive)
                    .Select(d => new { d.Environment, d.FiscalDayStatus }).ToListAsync();
                model.TestDevices = devices.Count(d => d.Environment == PlatformType.Simulation);
                model.LiveDevices = devices.Count(d => d.Environment == PlatformType.Production);
                model.OpenFiscalDays = devices.Count(d => d.FiscalDayStatus != FiscalDayStatus.FiscalDayClosed);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The database could not be read");
                model.DatabaseOk = false;
            }

            return View(model);
        }

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }
    }
}
