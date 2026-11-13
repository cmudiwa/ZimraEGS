using Microsoft.AspNetCore.Mvc;
using ZimraEGS.Models;
using ZimraEGS.Services;

namespace ZimraEGS.Controllers
{
    public class FiscalDeviceController : Controller
    {
        private readonly DeviceStore _store;
        private readonly FiscalService _fiscal;

        public FiscalDeviceController(DeviceStore store, FiscalService fiscal)
        {
            _store = store;
            _fiscal = fiscal;
        }

        /// <summary>Closes the open fiscal day on request. Days are also closed automatically every night.</summary>
        [HttpPost]
        public async Task<IActionResult> CloseDay([FromForm] string deviceLink, [FromForm] string? referrer)
        {
            var device = await _store.FindByLinkAsync(deviceLink);
            if (device == null)
            {
                return View("Problem", new ProblemViewModel
                {
                    Referrer = referrer ?? string.Empty,
                    Title = "Unknown device",
                    Problems = ["The device link is not valid. Relay an invoice from Manager and try again."]
                });
            }

            var result = await _fiscal.CloseDayAsync(device.Id);

            return View("DayResult", new DayResultViewModel
            {
                Referrer = referrer ?? string.Empty,
                Success = result.Success,
                Message = result.Message,
                FiscalDayNo = device.FiscalDayNo
            });
        }
    }
}
