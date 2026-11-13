using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using ZimraEGS.ApiClient.Models;
using ZimraEGS.Data;
using ZimraEGS.Models;
using ZimraEGS.Services;

namespace ZimraEGS.Controllers
{
    public class RelayController : Controller
    {
        private readonly DeviceStore _store;
        private readonly FiscalService _fiscal;
        private readonly ILogger<RelayController> _logger;

        public RelayController(DeviceStore store, FiscalService fiscal, ILogger<RelayController> logger)
        {
            _store = store;
            _fiscal = fiscal;
            _logger = logger;
        }

        /// <summary>
        /// Entry point of Manager's Relay button. One click fiscalises the invoice: the fiscal day is opened
        /// when needed, the receipt is sent to ZIMRA, and the result page stores the outcome back in Manager.
        /// </summary>
        [HttpPost("relay")]
        public async Task<IActionResult> ProcessFormData([FromForm] Dictionary<string, string> formData)
        {
            string referrer = formData.GetValueOrDefault("Referrer") ?? string.Empty;

            try
            {
                var relay = new RelayData(formData);

                var device = await _store.ResolveAsync(relay);
                if (device == null)
                {
                    if (DeviceLink.IsLink(relay.DeviceLinkRaw))
                    {
                        return ShowProblem(referrer, "This business is linked to a device this server does not know",
                            "The device link stored in Manager does not match any device registered on this server. Check that the Relay URL points to the server the device was registered on.");
                    }

                    // No device yet: start registration.
                    TempData["SetupViewModel"] = JsonConvert.SerializeObject(new SetupViewModel
                    {
                        Referrer = relay.Referrer,
                        Api = relay.Api,
                        Token = relay.Token,
                        BusinessDetailsJson = relay.BusinessDetailJson
                    });
                    return RedirectToAction("Register", "Setup");
                }

                _logger.LogInformation("Relay for device {DeviceID} ({Environment}) invoice {FormKey}", device.DeviceID, device.Environment, relay.FormKey);

                // The private key is still in Manager: hand out a link so the result page can replace it.
                string deviceLink = DeviceLink.IsLink(relay.DeviceLinkRaw) ? relay.DeviceLinkRaw : await _store.IssueLinkAsync(device);
                bool replaceLegacyBlob = !DeviceLink.IsLink(relay.DeviceLinkRaw);

                var model = new ReceiptResultViewModel
                {
                    Referrer = relay.Referrer,
                    Token = relay.Token,
                    DeviceLink = deviceLink,
                };

                if (replaceLegacyBlob)
                {
                    model.ManagerCalls.Add(ManagerWriteBack.BusinessLink(relay.Api, relay.BusinessDetailJson, deviceLink));
                }

                // Fiscalised by the previous version, before receipts were kept on this server.
                if (!string.IsNullOrEmpty(relay.ApprovalStatus) && await _fiscal.FindRecordedReceiptAsync(device.Id, relay.FormKey) == null)
                {
                    model.Status = relay.ApprovalStatus;
                    model.AlreadyFiscalised = true;
                    model.InvoiceNo = relay.ManagerInvoice?.Reference ?? string.Empty;
                    model.QrCode = relay.ReceiptQrCode ?? string.Empty;
                    model.VerificationCode = relay.VerificationCode ?? string.Empty;
                    model.ValidationErrors = relay.ReceiptReference?.SubmitReceiptResponse?.ValidationErrors?.ToList() ?? [];
                    FillDay(model, device);
                    return View("Result", model);
                }

                var result = await _fiscal.FiscaliseAsync(device.Id, relay);
                if (!result.Success)
                {
                    return ShowProblem(referrer, result.ProblemTitle ?? "The invoice could not be sent to ZIMRA", [.. result.Problems]);
                }

                var row = result.Receipt!;
                model.Status = row.Status;
                model.AlreadyFiscalised = result.AlreadyFiscalised;
                model.InvoiceNo = row.InvoiceNo;
                model.ReceiptType = row.ReceiptType.ToString();
                model.ReceiptDate = row.ReceiptDate.Replace('T', ' ');
                model.Currency = row.Currency;
                model.Total = row.Total;
                model.FiscalDayNo = row.FiscalDayNo;
                model.ReceiptGlobalNo = row.ReceiptGlobalNo;
                model.QrCode = row.QrCode;
                model.VerificationCode = row.VerificationCode;
                model.ValidationErrors = ParseValidationErrors(row);
                model.ManagerCalls.Add(ManagerWriteBack.Invoice(relay, device, row));
                model.AutoReturn = !result.AlreadyFiscalised && row.Status == ReceiptStatus.Approved && model.ValidationErrors.Count == 0;
                FillDay(model, device);

                return View("Result", model);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Relay failed");
                return ShowProblem(referrer, "Something went wrong", ex.Message);
            }
        }

        private static void FillDay(ReceiptResultViewModel model, Device device)
        {
            model.FiscalDayStatus = device.FiscalDayStatus.ToString();
            model.LastCloseDayError = device.LastCloseDayError;
            if (model.FiscalDayNo == 0) model.FiscalDayNo = device.FiscalDayNo;
        }

        private static List<ValidationError> ParseValidationErrors(FiscalReceipt row)
        {
            if (string.IsNullOrEmpty(row.ResponseJson)) return [];
            try
            {
                return JsonConvert.DeserializeObject<SubmitReceiptResponse>(row.ResponseJson)?.ValidationErrors?.ToList() ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }

        private ViewResult ShowProblem(string referrer, string title, params string[] problems)
        {
            return View("Problem", new ProblemViewModel { Referrer = referrer, Title = title, Problems = [.. problems] });
        }
    }
}
