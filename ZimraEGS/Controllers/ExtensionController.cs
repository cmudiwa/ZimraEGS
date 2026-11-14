using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ZimraEGS.ApiClient.Enums;
using ZimraEGS.ApiClient.Models;
using ZimraEGS.Data;
using ZimraEGS.Helpers;
using ZimraEGS.Models;
using ZimraEGS.Services;

namespace ZimraEGS.Controllers
{
    /// <summary>
    /// Backend of the Manager extension. Manager shows the extension page in a panel on the invoice view;
    /// the page reads the document through Manager's API bridge and posts it here.
    /// </summary>
    public class ExtensionController : Controller
    {
        private readonly DeviceStore _store;
        private readonly FiscalService _fiscal;
        private readonly IWebHostEnvironment _environment;
        private readonly ILogger<ExtensionController> _logger;

        public ExtensionController(DeviceStore store, FiscalService fiscal, IWebHostEnvironment environment, ILogger<ExtensionController> logger)
        {
            _store = store;
            _fiscal = fiscal;
            _environment = environment;
            _logger = logger;
        }

        /// <summary>The page to enter as the extension's Endpoint in Manager.</summary>
        [HttpGet("extension")]
        public IActionResult Index()
        {
            // Manager loads the page inside a frame on its own origin.
            Response.Headers.ContentSecurityPolicy = "frame-ancestors *";
            Response.Headers.CacheControl = "no-cache";
            return PhysicalFile(Path.Combine(_environment.WebRootPath, "extension", "index.html"), "text/html; charset=utf-8");
        }

        private async Task<JObject> ReadBodyAsync()
        {
            using var reader = new StreamReader(Request.Body);
            string text = await reader.ReadToEndAsync();
            return string.IsNullOrWhiteSpace(text) ? new JObject() : JObject.Parse(text);
        }

        private ContentResult Json(object value) => Content(JsonConvert.SerializeObject(value), "application/json");

        private sealed class Context
        {
            public RelayData Relay = null!;
            public Device? Device;
            public string DeviceLink = string.Empty;
            public List<ManagerApiCall> Calls = [];
            public bool UnknownLink;
        }

        private async Task<Context> LoadAsync(JObject body)
        {
            string path = (string?)body["path"] ?? string.Empty;
            var document = body["document"] as JObject ?? throw new InvalidOperationException("The document could not be read from Manager.");

            var context = new Context
            {
                Relay = RelayData.FromExtension(path, (string?)body["key"] ?? string.Empty, document, body["business"] as JObject, (string?)body["baseCurrency"])
            };

            context.Device = await _store.ResolveAsync(context.Relay);
            if (context.Device == null)
            {
                context.UnknownLink = DeviceLink.IsLink(context.Relay.DeviceLinkRaw);
                return context;
            }

            if (DeviceLink.IsLink(context.Relay.DeviceLinkRaw))
            {
                context.DeviceLink = context.Relay.DeviceLinkRaw;
            }
            else
            {
                // The private key is still stored in Manager: replace it with a link to the device.
                context.DeviceLink = await _store.IssueLinkAsync(context.Device);
                context.Calls.Add(ManagerWriteBack.BusinessLinkPatch(context.DeviceLink));
            }

            return context;
        }

        private static object Day(Device device) => new
        {
            status = device.FiscalDayStatus.ToString(),
            open = device.FiscalDayStatus != FiscalDayStatus.FiscalDayClosed,
            number = device.FiscalDayNo,
            lastCloseError = device.LastCloseDayError,
        };

        private static object CallsOf(IEnumerable<ManagerApiCall> calls) => calls.Select(c => new { method = c.Method, path = c.ApiUrl, body = c.Body }).ToList();

        private object Done(Context context, FiscalReceipt row, bool alreadyFiscalised)
        {
            var errors = new List<object>();
            if (!string.IsNullOrEmpty(row.ResponseJson))
            {
                try
                {
                    var parsed = JsonConvert.DeserializeObject<SubmitReceiptResponse>(row.ResponseJson);
                    errors.AddRange((parsed?.ValidationErrors ?? []).Select(e => (object)new { code = e.ValidationErrorCode, color = e.ValidationErrorColor, text = e.ValidationErrorText }));
                }
                catch (JsonException) { }
            }

            context.Calls.Add(ManagerWriteBack.InvoicePatch(context.Device!, row));

            return new
            {
                state = "done",
                alreadyFiscalised,
                approved = row.Status == ReceiptStatus.Approved,
                status = row.Status,
                invoiceNo = row.InvoiceNo,
                receiptType = row.ReceiptType.ToString(),
                receiptDate = row.ReceiptDate.Replace('T', ' '),
                currency = row.Currency,
                total = row.Total,
                fiscalDayNo = row.FiscalDayNo,
                receiptGlobalNo = row.ReceiptGlobalNo,
                qrCode = row.QrCode,
                verificationCode = row.VerificationCode,
                validationErrors = errors,
                calls = CallsOf(context.Calls),
                deviceLink = context.DeviceLink,
                defaultMoneyType = context.Device!.DefaultMoneyType,
                day = Day(context.Device),
            };
        }

        private object NotReady(Context context, string title, IEnumerable<string> problems) => new
        {
            state = "problem",
            title,
            problems = problems.ToList(),
            calls = CallsOf(context.Calls),
            deviceLink = context.DeviceLink,
            defaultMoneyType = context.Device?.DefaultMoneyType,
            day = context.Device == null ? null : Day(context.Device),
        };

        /// <summary>Checks the open document and says what the page should show. Sends nothing to ZIMRA.</summary>
        [HttpPost("extension/api/prepare")]
        public async Task<IActionResult> Prepare()
        {
            try
            {
                var context = await LoadAsync(await ReadBodyAsync());
                if (context.Device == null)
                {
                    return Json(new { state = context.UnknownLink ? "unknown-device" : "setup" });
                }

                var recorded = await _fiscal.FindRecordedReceiptAsync(context.Device.Id, context.Relay.FormKey);
                if (recorded != null)
                {
                    return Json(Done(context, recorded, alreadyFiscalised: true));
                }

                // Fiscalised by a version that kept nothing on the server: show what Manager holds.
                if (!string.IsNullOrEmpty(context.Relay.ApprovalStatus))
                {
                    return Json(new
                    {
                        state = "done",
                        alreadyFiscalised = true,
                        approved = context.Relay.ApprovalStatus == ReceiptStatus.Approved,
                        status = context.Relay.ApprovalStatus,
                        invoiceNo = context.Relay.ManagerInvoice?.Reference,
                        qrCode = context.Relay.ReceiptQrCode,
                        verificationCode = context.Relay.VerificationCode,
                        validationErrors = Array.Empty<object>(),
                        calls = CallsOf(context.Calls),
                        deviceLink = context.DeviceLink,
                        defaultMoneyType = context.Device.DefaultMoneyType,
                        day = Day(context.Device),
                    });
                }

                var draft = await _fiscal.PreviewAsync(context.Device.Id, context.Relay);
                if (draft.Problems.Count > 0)
                {
                    return Json(NotReady(context, "This document cannot be sent to ZIMRA yet", draft.Problems));
                }

                var receipt = draft.Receipt;
                return Json(new
                {
                    state = "ready",
                    summary = new
                    {
                        receiptType = receipt.ReceiptType.ToString(),
                        currency = receipt.ReceiptCurrency,
                        total = receipt.ReceiptTotal,
                        tax = receipt.ReceiptTaxes.Sum(t => t.TaxAmount),
                        lines = receipt.ReceiptLines.Count,
                        buyer = receipt.BuyerData?.BuyerRegisterName,
                        payments = receipt.ReceiptPayments.Select(p => new { type = p.MoneyTypeCode.ToString(), amount = p.PaymentAmount }),
                    },
                    calls = CallsOf(context.Calls),
                    deviceLink = context.DeviceLink,
                    defaultMoneyType = context.Device.DefaultMoneyType,
                    day = Day(context.Device),
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Extension prepare failed");
                return Json(new { state = "problem", title = "Something went wrong", problems = new[] { ex.Message } });
            }
        }

        /// <summary>Sends the open document to ZIMRA. Safe to repeat: a document is never fiscalised twice.</summary>
        [HttpPost("extension/api/fiscalise")]
        public async Task<IActionResult> Fiscalise()
        {
            try
            {
                var context = await LoadAsync(await ReadBodyAsync());
                if (context.Device == null)
                {
                    return Json(new { state = context.UnknownLink ? "unknown-device" : "setup" });
                }

                if (!string.IsNullOrEmpty(context.Relay.ApprovalStatus) &&
                    await _fiscal.FindRecordedReceiptAsync(context.Device.Id, context.Relay.FormKey) == null)
                {
                    return Json(NotReady(context, "Already fiscalised", ["This document was fiscalised earlier and cannot be sent again."]));
                }

                _logger.LogInformation("Extension fiscalise for device {DeviceID} ({Environment}) document {FormKey}", context.Device.DeviceID, context.Device.Environment, context.Relay.FormKey);

                var result = await _fiscal.FiscaliseAsync(context.Device.Id, context.Relay);
                if (!result.Success)
                {
                    return Json(NotReady(context, result.ProblemTitle ?? "The document could not be sent to ZIMRA", result.Problems));
                }

                // Re-read the device so the day shown is the one the receipt went into.
                context.Device = await _store.FindAsync(context.Device.Environment, context.Device.DeviceID) ?? context.Device;
                return Json(Done(context, result.Receipt!, result.AlreadyFiscalised));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Extension fiscalise failed");
                return Json(new { state = "problem", title = "Something went wrong", problems = new[] { ex.Message } });
            }
        }

        [HttpPost("extension/api/closeday")]
        public async Task<IActionResult> CloseDay()
        {
            var body = await ReadBodyAsync();
            var device = await _store.FindByLinkAsync((string?)body["deviceLink"]);
            if (device == null) return Json(new { success = false, message = "Unknown device." });

            var result = await _fiscal.CloseDayAsync(device.Id);
            return Json(new { success = result.Success, message = result.Message });
        }

        /// <summary>
        /// The tax codes for the taxes ZIMRA currently allows on the device, to create or update in Manager.
        /// ZIMRA can change a device's taxes; the configuration is fetched fresh so the codes match.
        /// </summary>
        [HttpPost("extension/api/taxcodes")]
        public async Task<IActionResult> TaxCodes()
        {
            var body = await ReadBodyAsync();
            var device = await _store.FindByLinkAsync((string?)body["deviceLink"]);
            if (device == null) return Json(new { success = false, message = "Unknown device." });

            bool refreshed = await _fiscal.RefreshConfigAsync(device.Id);
            device = await _store.FindAsync(device.Environment, device.DeviceID) ?? device;

            var calls = ManagerSetupData.BuildApiCalls(device).Where(c => c.ApiUrl.StartsWith("/api3/tax-code-form/", StringComparison.Ordinal)).ToList();
            return Json(new
            {
                success = calls.Count > 0,
                message = calls.Count > 0 ? null : "ZIMRA's tax list could not be read. Try again in a moment.",
                refreshed,
                names = calls.Select(c => (string?)c.Body?["name"]).ToList(),
                calls = CallsOf(calls),
            });
        }

        [HttpPost("extension/api/settings")]
        public async Task<IActionResult> Settings()
        {
            var body = await ReadBodyAsync();
            var device = await _store.FindByLinkAsync((string?)body["deviceLink"]);
            if (device == null) return Json(new { success = false, message = "Unknown device." });

            int? moneyType = body["defaultMoneyType"]?.Type == JTokenType.Integer ? (int)body["defaultMoneyType"]! : null;
            if (moneyType != null && !Enum.IsDefined(typeof(MoneyType), moneyType.Value)) moneyType = null;

            await _store.SetDefaultMoneyTypeAsync(device, moneyType);
            return Json(new { success = true, defaultMoneyType = moneyType });
        }
    }
}
