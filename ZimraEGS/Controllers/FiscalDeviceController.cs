using Microsoft.AspNetCore.Mvc;
using ZimraEGS.ApiClient.Enums;
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
                Hint = result.Success ? "A new fiscal day opens automatically with the next invoice you relay." : null,
                FiscalDayNo = device.FiscalDayNo
            });
        }

        /// <summary>Changes the payment type assumed for invoices that do not state one.</summary>
        [HttpPost]
        public async Task<IActionResult> Settings([FromForm] string deviceLink, [FromForm] int? defaultMoneyType, [FromForm] string? referrer)
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

            if (defaultMoneyType != null && !Enum.IsDefined(typeof(MoneyType), defaultMoneyType.Value))
            {
                defaultMoneyType = null;
            }

            await _store.SetDefaultMoneyTypeAsync(device, defaultMoneyType);

            return View("DayResult", new DayResultViewModel
            {
                Referrer = referrer ?? string.Empty,
                Title = "Default payment type",
                Success = true,
                Message = defaultMoneyType == null
                    ? "No payment type is assumed. Choose one on each invoice."
                    : $"Invoices without a payment type are now reported as paid by {(MoneyType)defaultMoneyType.Value}.",
                Hint = "Go back to Manager and click Relay on the invoice again."
            });
        }
    }
}
