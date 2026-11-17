using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using ZimraEGS.ApiClient.Enums;
using ZimraEGS.ApiClient.Models;
using ZimraEGS.Data;
using ZimraEGS.Models;
using ZimraEGS.Services;

namespace ZimraEGS.Controllers
{
    /// <summary>Fiscalising invoices kept in Excel: download the template, upload it, check, send, print.</summary>
    [Authorize, Route("excel")]
    public class ExcelController : Controller
    {
        public const long MaxUploadBytes = 5 * 1024 * 1024;

        private readonly FiscalDbContext _db;
        private readonly ExcelService _excel;
        private readonly BillingService _billing;
        private readonly PrivacyService _privacy;
        private readonly DeviceRegistration _registration;
        private readonly ILogger<ExcelController> _logger;

        public ExcelController(FiscalDbContext db, ExcelService excel, BillingService billing, PrivacyService privacy,
            DeviceRegistration registration, ILogger<ExcelController> logger)
        {
            _db = db;
            _excel = excel;
            _billing = billing;
            _privacy = privacy;
            _registration = registration;
            _logger = logger;
        }

        private int AccountId => AccountController.CurrentAccountId(User)!.Value;
        private string Me => User.FindFirstValue(ClaimTypes.Email) ?? "unknown";
        private string? Ip => HttpContext.Connection.RemoteIpAddress?.ToString();

        private Task<List<Device>> MyDevicesAsync() =>
            _db.Devices.AsNoTracking().Where(d => d.AccountId == AccountId && d.IsActive).OrderBy(d => d.Environment).ThenBy(d => d.DeviceID).ToListAsync();

        private Task<Device?> MyDeviceAsync(int id) =>
            _db.Devices.FirstOrDefaultAsync(d => d.Id == id && d.AccountId == AccountId && d.IsActive);

        private Task<ExcelBatch?> MyBatchAsync(string publicId) =>
            _db.ExcelBatches.FirstOrDefaultAsync(b => b.PublicId == publicId && b.AccountId == AccountId);

        [HttpGet("")]
        public async Task<IActionResult> Index(int? device, string? error, string? message)
        {
            var devices = await MyDevicesAsync();
            var selected = devices.FirstOrDefault(d => d.Id == device) ?? devices.FirstOrDefault();
            var batches = await _db.ExcelBatches.AsNoTracking().Where(b => b.AccountId == AccountId)
                .OrderByDescending(b => b.CreatedUtc).Take(10)
                .Select(b => new ExcelBatch { PublicId = b.PublicId, FileName = b.FileName, CreatedUtc = b.CreatedUtc, SentUtc = b.SentUtc, DeviceRowId = b.DeviceRowId })
                .ToListAsync();
            return View(new ExcelPageModel
            {
                Devices = devices.Select(d => (d, _billing.StatusOf(d))).ToList(),
                Selected = selected,
                Batches = batches,
                BillingOptions = _billing.Options,
                RetentionDays = _privacy.Options.ExcelRetentionDays,
                Error = error,
                Message = message,
            });
        }

        // ------------------------------------------------------------------ connecting a device

        [HttpGet("connect")]
        public IActionResult Connect() => View(new ExcelConnectModel());

        [HttpPost("connect"), ValidateAntiForgeryToken]
        public async Task<IActionResult> Connect(ExcelConnectModel form)
        {
            if (!Enum.TryParse(form.Environment, out PlatformType environment) || form.DeviceID <= 0
                || string.IsNullOrWhiteSpace(form.SerialNumber) || string.IsNullOrWhiteSpace(form.ActivationKey))
            {
                form.Error = "Fill in the Device ID, Serial Number and Activation Key that ZIMRA gave you.";
                return View(form);
            }

            // A device already used by another account is not taken over.
            var existing = await _db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.DeviceID == form.DeviceID && d.Environment == environment);
            if (existing?.AccountId is { } owner && owner != AccountId)
            {
                form.Error = "This device is linked to another account. Ask the owner of that account, or contact us.";
                return View(form);
            }

            try
            {
                var (device, info, error) = await _registration.RegisterAsync(environment, form.DeviceID, form.SerialNumber, form.ActivationKey,
                    null, null, form.DefaultMoneyType);
                if (device == null)
                {
                    form.Error = error;
                    return View(form);
                }
                var account = await _db.Accounts.FirstAsync(a => a.Id == AccountId);
                if (device.AccountId != account.Id) await _billing.LinkDeviceAsync(device, account);
                await _privacy.LogAsync(Me, "device connected", AccountId, $"device {device.DeviceID} ({device.Environment}) for Excel", Ip);
                return RedirectToAction(nameof(Index), new { device = device.Id, message = $"Device {device.DeviceID} of {info!.TaxPayerName} is connected." });
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                form.Error = "ZIMRA could not be reached: " + ex.Message + " Try again in a moment.";
                return View(form);
            }
        }

        [HttpPost("device/{id:int}/payment-type"), ValidateAntiForgeryToken]
        public async Task<IActionResult> DefaultPayment(int id, int? defaultMoneyType)
        {
            var device = await MyDeviceAsync(id);
            if (device == null) return NotFound();
            device.DefaultMoneyType = defaultMoneyType is { } m && Enum.IsDefined(typeof(MoneyType), m) ? m : null;
            await _db.SaveChangesAsync();
            return RedirectToAction(nameof(Index), new { device = id, message = "Default payment type saved." });
        }

        // ------------------------------------------------------------------ template and upload

        [HttpGet("template")]
        public async Task<IActionResult> Template(int device)
        {
            var d = await MyDeviceAsync(device);
            if (d == null) return NotFound();
            if (DeviceStore.GetConfig(d) == null) return RedirectToAction(nameof(Index), new { device, error = "ZIMRA's settings for this device are not known yet. Connect the device again." });
            return File(ExcelInvoices.BuildTemplate(d), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"fiscal-invoices-{d.DeviceID}.xlsx");
        }

        [HttpPost("upload"), ValidateAntiForgeryToken, RequestSizeLimit(MaxUploadBytes + 64 * 1024)]
        public async Task<IActionResult> Upload(int device, IFormFile? file)
        {
            var d = await MyDeviceAsync(device);
            if (d == null) return NotFound();
            if (file == null || file.Length == 0) return RedirectToAction(nameof(Index), new { device, error = "Choose the workbook to upload." });
            if (file.Length > MaxUploadBytes) return RedirectToAction(nameof(Index), new { device, error = "The file is larger than 5 MB. Upload fewer invoices at a time." });
            if (!file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) return RedirectToAction(nameof(Index), new { device, error = "Upload an Excel workbook (.xlsx). In Excel: File, Save As, Excel Workbook." });

            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer);
            var batch = new ExcelBatch
            {
                AccountId = AccountId,
                DeviceRowId = d.Id,
                FileName = Path.GetFileName(file.FileName),
                Content = buffer.ToArray(),
            };
            _db.ExcelBatches.Add(batch);
            await _db.SaveChangesAsync();
            await _privacy.LogAsync(Me, "workbook uploaded", AccountId, $"{batch.FileName} for device {d.DeviceID}", Ip);
            return RedirectToAction(nameof(Batch), new { id = batch.PublicId });
        }

        // ------------------------------------------------------------------ checking and sending

        private (List<ExcelDocument> docs, List<string> problems) ReadBatch(ExcelBatch batch, Device device) =>
            ExcelInvoices.Read(new MemoryStream(batch.Content), device);

        [HttpGet("batch/{id}")]
        public async Task<IActionResult> Batch(string id)
        {
            var batch = await MyBatchAsync(id);
            if (batch == null) return NotFound();
            var device = await MyDeviceAsync(batch.DeviceRowId);
            if (device == null) return NotFound();

            var model = new ExcelBatchModel { Batch = batch, Device = device, Billing = _billing.StatusOf(device), BillingOptions = _billing.Options };
            if (!string.IsNullOrEmpty(batch.ResultsJson))
            {
                model.Results = JsonConvert.DeserializeObject<Dictionary<string, ExcelResult>>(batch.ResultsJson)!.Values.ToList();
                return View(model);
            }

            var (docs, problems) = ReadBatch(batch, device);
            model.FileProblems = problems;
            try
            {
                model.Checks = await _excel.CheckAsync(device, docs);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                model.FileProblems.Add("ZIMRA could not be reached to check the device's taxes. Try again in a moment.");
            }
            return View(model);
        }

        [HttpPost("batch/{id}/send"), ValidateAntiForgeryToken]
        public async Task<IActionResult> Send(string id)
        {
            var batch = await MyBatchAsync(id);
            if (batch == null) return NotFound();
            var device = await MyDeviceAsync(batch.DeviceRowId);
            if (device == null) return NotFound();
            if (!string.IsNullOrEmpty(batch.ResultsJson)) return RedirectToAction(nameof(Batch), new { id });

            var (docs, problems) = ReadBatch(batch, device);
            if (problems.Count > 0) return RedirectToAction(nameof(Batch), new { id });

            var results = await _excel.SendAsync(device, docs);
            batch.ResultsJson = JsonConvert.SerializeObject(results);
            batch.SentUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            int sent = results.Values.Count(r => r.Status is ReceiptStatus.Approved or ReceiptStatus.Rejected);
            await _privacy.LogAsync(Me, "workbook sent", AccountId, $"{batch.FileName}: {sent} of {results.Count} document(s) fiscalised on device {device.DeviceID}", Ip);
            _logger.LogInformation("Excel batch {Batch}: {Sent} of {Count} fiscalised on device {DeviceID}", batch.Id, sent, results.Count, device.DeviceID);
            return RedirectToAction(nameof(Batch), new { id });
        }

        /// <summary>Checks the workbook again, for example after ZIMRA could not be reached. Nothing already sent is sent twice.</summary>
        [HttpPost("batch/{id}/again"), ValidateAntiForgeryToken]
        public async Task<IActionResult> Again(string id)
        {
            var batch = await MyBatchAsync(id);
            if (batch == null) return NotFound();
            batch.ResultsJson = string.Empty;
            batch.SentUtc = null;
            await _db.SaveChangesAsync();
            return RedirectToAction(nameof(Batch), new { id });
        }

        // ------------------------------------------------------------------ results

        [HttpGet("batch/{id}/workbook")]
        public async Task<IActionResult> Workbook(string id)
        {
            var batch = await MyBatchAsync(id);
            if (batch == null || string.IsNullOrEmpty(batch.ResultsJson)) return NotFound();
            var results = JsonConvert.DeserializeObject<Dictionary<string, ExcelResult>>(batch.ResultsJson)!;
            string name = Path.GetFileNameWithoutExtension(batch.FileName) + " - fiscalised.xlsx";
            return File(ExcelInvoices.WriteResults(batch.Content, results), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name);
        }

        /// <summary>Printable fiscal documents in ZIMRA's layout: one (?doc=reference) or all of the batch.</summary>
        [HttpGet("batch/{id}/print")]
        public async Task<IActionResult> Print(string id, string? doc)
        {
            var batch = await MyBatchAsync(id);
            if (batch == null || string.IsNullOrEmpty(batch.ResultsJson)) return NotFound();
            var device = await _db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == batch.DeviceRowId && d.AccountId == AccountId);
            if (device == null) return NotFound();

            var results = JsonConvert.DeserializeObject<Dictionary<string, ExcelResult>>(batch.ResultsJson)!.Values
                .Where(r => r.ReceiptRowId != null && (doc == null || string.Equals(r.Reference, doc, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            var ids = results.Select(r => r.ReceiptRowId!.Value).ToList();
            var rows = await _db.Receipts.AsNoTracking().Where(r => ids.Contains(r.Id) && r.DeviceRowId == device.Id).ToDictionaryAsync(r => r.Id);
            var documents = results.Where(r => rows.ContainsKey(r.ReceiptRowId!.Value))
                .Select(r => ExcelPrint.ViewData(device, rows[r.ReceiptRowId!.Value], r.Reference)).ToList();
            if (documents.Count == 0) return NotFound();

            Response.Headers.CacheControl = "no-store";
            return Content(ExcelPrint.Page(documents), "text/html; charset=utf-8");
        }
    }
}
