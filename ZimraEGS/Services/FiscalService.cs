using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.Net;
using ZimraEGS.ApiClient.Enums;
using ZimraEGS.ApiClient.Helpers;
using ZimraEGS.ApiClient.Models;
using ZimraEGS.Data;
using ZimraEGS.Helpers;
using ZimraEGS.Models;

namespace ZimraEGS.Services
{
    /// <summary>Serialises all work on one device: its receipts form a chain, so two can never be built at once.</summary>
    public class DeviceLocks
    {
        private readonly ConcurrentDictionary<int, SemaphoreSlim> _locks = new();

        public async Task<IDisposable> AcquireAsync(int deviceRowId)
        {
            var semaphore = _locks.GetOrAdd(deviceRowId, _ => new SemaphoreSlim(1, 1));
            await semaphore.WaitAsync();
            return new Releaser(semaphore);
        }

        private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
        {
            private int _released;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0) semaphore.Release();
            }
        }
    }

    public class FiscalServiceOptions
    {
        /// <summary>How often and how long to wait for FDMS to finish closing a fiscal day.</summary>
        public int CloseDayPollAttempts { get; set; } = 8;
        public int CloseDayPollDelayMs { get; set; } = 2500;

        /// <summary>Renew the device certificate when it expires within this many days.</summary>
        public int CertificateRenewalDays { get; set; } = 30;
    }

    public class FiscaliseResult
    {
        /// <summary>The receipt as recorded by FDMS. Null when nothing was recorded.</summary>
        public FiscalReceipt? Receipt { get; set; }

        /// <summary>True when the invoice had already been fiscalised and the stored result is returned.</summary>
        public bool AlreadyFiscalised { get; set; }

        public string? ProblemTitle { get; set; }
        public List<string> Problems { get; set; } = [];

        public bool Success => Receipt != null;

        public static FiscaliseResult Problem(string title, params string[] details) => new() { ProblemTitle = title, Problems = [.. details] };
    }

    public class DayResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public class FiscalService
    {
        private readonly FiscalDbContext _db;
        private readonly DeviceStore _store;
        private readonly IFdmsClientFactory _clientFactory;
        private readonly DeviceLocks _locks;
        private readonly FiscalServiceOptions _options;
        private readonly ILogger<FiscalService> _logger;

        private static readonly JsonSerializerSettings FdmsJson = new()
        {
            Converters = [new LocalDateTimeConverter()],
            DateParseHandling = DateParseHandling.None
        };

        public FiscalService(FiscalDbContext db, DeviceStore store, IFdmsClientFactory clientFactory, DeviceLocks locks,
            Microsoft.Extensions.Options.IOptions<FiscalServiceOptions> options, ILogger<FiscalService> logger)
        {
            _db = db;
            _store = store;
            _clientFactory = clientFactory;
            _locks = locks;
            _options = options.Value;
            _logger = logger;
        }

        private IFdmsClient CreateClient(Device device)
        {
            return _clientFactory.CreateDeviceClient(device.Environment, device.DeviceID, device.ModelName, device.ModelVersion, _store.Unprotect(device.ProtectedPfx));
        }

        public static Receipt ParseReceipt(FiscalReceipt row) => JsonConvert.DeserializeObject<Receipt>(row.ReceiptJson, FdmsJson)!;

        public Task<FiscalReceipt?> FindRecordedReceiptAsync(int deviceRowId, string formKey)
        {
            return _db.Receipts.AsNoTracking()
                .Where(r => r.DeviceRowId == deviceRowId && r.FormKey == formKey && r.Status != ReceiptStatus.Pending)
                .OrderByDescending(r => r.Id)
                .FirstOrDefaultAsync();
        }

        // ---------------------------------------------------------------------------------------
        // Fiscalising an invoice
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// Sends the relayed invoice to FDMS in one step: resolves any unfinished receipt, opens the fiscal day
        /// when needed, numbers and signs the receipt, submits it and records the outcome.
        /// Safe to call again for the same invoice; it is never fiscalised twice.
        /// </summary>
        public async Task<FiscaliseResult> FiscaliseAsync(int deviceRowId, RelayData relay)
        {
            using var deviceLock = await _locks.AcquireAsync(deviceRowId);

            var device = await _db.Devices.FirstAsync(d => d.Id == deviceRowId);

            var existing = await FindRecordedReceiptAsync(deviceRowId, relay.FormKey);
            if (existing != null)
            {
                return new FiscaliseResult { Receipt = existing, AlreadyFiscalised = true };
            }

            using var client = CreateClient(device);

            try
            {
                // 1. A receipt whose outcome is unknown blocks the chain until FDMS has answered for it.
                var pendingProblem = await ResolvePendingAsync(device, client);
                if (pendingProblem != null) return pendingProblem;

                existing = await FindRecordedReceiptAsync(deviceRowId, relay.FormKey);
                if (existing != null)
                {
                    return new FiscaliseResult { Receipt = existing };
                }

                // 2. Make sure a fiscal day is open.
                var (status, statusProblem) = await GetStatusAsync(client);
                if (status == null) return statusProblem!;

                var dayProblem = await EnsureDayOpenAsync(device, client, status);
                if (dayProblem != null) return dayProblem;

                await EnsureConfigAsync(device, client, force: false);

                // 3. Build and check the receipt before anything is numbered.
                var info = DeviceStore.GetInfo(device);
                var config = DeviceStore.GetConfig(device);
                var draft = ReceiptBuilder.Build(relay, config, !string.IsNullOrWhiteSpace(config?.VatNumber ?? info.VatNumber),
                    device.DefaultMoneyType is { } m && Enum.IsDefined(typeof(MoneyType), m) ? (MoneyType)m : null);
                if (draft.Problems.Count > 0)
                {
                    return new FiscaliseResult
                    {
                        ProblemTitle = "This invoice cannot be sent to ZIMRA yet",
                        Problems = draft.Problems
                    };
                }

                // 4. Number, sign and store the receipt, then submit it.
                var row = PrepareReceipt(device, relay, draft.Receipt, status, config?.QrUrl ?? info.QrUrl);
                _db.Receipts.Add(row);
                await _db.SaveChangesAsync();

                return await SubmitAsync(device, client, row, isRetry: false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _logger.LogWarning(ex, "FDMS unreachable for device {DeviceID}", device.DeviceID);
                return FiscaliseResult.Problem("ZIMRA could not be reached",
                    "The ZIMRA server did not respond. Nothing was lost: relay the invoice again in a moment.",
                    ex.Message);
            }
        }

        private FiscalReceipt PrepareReceipt(Device device, RelayData relay, Receipt receipt, GetStatusResponse status, string? qrUrl)
        {
            var now = TimeZoneHelper.GetSouthAfricaTime();
            // FDMS works in whole seconds; the signed date must be exactly the date that is sent.
            var receiptDate = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, now.Offset);

            receipt.ReceiptCounter = device.LastReceiptCounter + 1;
            receipt.ReceiptGlobalNo = Math.Max(device.LastReceiptGlobalNo, status.LastReceiptGlobalNo) + 1;
            receipt.ReceiptDate = receiptDate;
            receipt.InvoiceNo = $"{device.FiscalDayNo:D4}-{receipt.ReceiptCounter:D4}";

            string privateKey = _store.Unprotect(device.ProtectedPrivateKey);
            string source = ReceiptSigner.ReceiptHashSource(device.DeviceID, receipt, device.LastReceiptHash);
            receipt.ReceiptDeviceSignature = ReceiptSigner.Sign(privateKey, source);

            if (!string.IsNullOrEmpty(device.DeviceCertificate))
            {
                using var certificate = RSA_CryptoHelper.ConvertCertificateFromBase64(device.DeviceCertificate);
                if (!RSA_CryptoHelper.VerifySignature(certificate, receipt.ReceiptDeviceSignature.Hash, receipt.ReceiptDeviceSignature.Signature))
                {
                    throw new InvalidOperationException("The device certificate does not belong to the device's private key. Register the device again.");
                }
            }

            object? reference = null;
            if (receipt.ReceiptType != ReceiptType.FiscalInvoice)
            {
                reference = new ReceiptReferenceInfo
                {
                    DeviceIDRef = relay.DeviceIDRef,
                    DeviceSNRef = relay.DeviceSNRef,
                    FiscalDayNoRef = relay.FiscalDayNoRef,
                    ReceiptCounterRef = relay.ReceiptCounterRef,
                    ReceiptGlobalNoRef = relay.ReceiptGlobalNoRef,
                    ReceiptRefNo = relay.ReceiptRefNo,
                    ReceiptRefDate = relay.ReceiptRefDate == DateTimeOffset.MinValue ? null : relay.ReceiptRefDate.ToString("yyyy-MM-dd HH:mm:ss")
                };
            }

            return new FiscalReceipt
            {
                DeviceRowId = device.Id,
                FormKey = relay.FormKey,
                FiscalDayNo = device.FiscalDayNo,
                ReceiptCounter = receipt.ReceiptCounter,
                ReceiptGlobalNo = receipt.ReceiptGlobalNo,
                ReceiptType = receipt.ReceiptType,
                Currency = receipt.ReceiptCurrency,
                InvoiceNo = receipt.InvoiceNo,
                ReceiptDate = TimeZoneHelper.ToFdmsString(receiptDate),
                Total = receipt.ReceiptTotal,
                ReceiptJson = JsonConvert.SerializeObject(receipt),
                Hash = Convert.ToBase64String(receipt.ReceiptDeviceSignature.Hash),
                Signature = Convert.ToBase64String(receipt.ReceiptDeviceSignature.Signature),
                QrCode = ReceiptSigner.QrCode(qrUrl ?? string.Empty, device.DeviceID, receiptDate, receipt.ReceiptGlobalNo, receipt.ReceiptDeviceSignature.Signature),
                VerificationCode = ReceiptSigner.VerificationCode(receipt.ReceiptDeviceSignature.Signature),
                Status = ReceiptStatus.Pending,
                RefJson = reference == null ? null : JsonConvert.SerializeObject(reference),
            };
        }

        /// <summary>
        /// Submits a stored receipt and records the outcome. The receipt stays pending when the outcome is unknown
        /// (no answer, or a server-side failure), so the identical receipt is sent again later.
        /// </summary>
        private async Task<FiscaliseResult> SubmitAsync(Device device, IFdmsClient client, FiscalReceipt row, bool isRetry)
        {
            var request = new SubmitReceiptRequest { Receipt = ParseReceipt(row) };

            ServerResponse response;
            try
            {
                response = await client.SubmitReceiptAsync(request);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _logger.LogWarning(ex, "SubmitReceipt got no answer for device {DeviceID} receipt {GlobalNo}", device.DeviceID, row.ReceiptGlobalNo);
                return FiscaliseResult.Problem("ZIMRA did not answer",
                    $"Receipt {row.InvoiceNo} was signed and saved but ZIMRA did not confirm it. It will be sent again automatically the next time you relay an invoice. Relay again in a moment.");
            }

            if (response.IsSuccess)
            {
                var submitResponse = response.GetContentAs<SubmitReceiptResponse>();
                MarkRecorded(device, row, submitResponse, response.ResponseContent);
                await _db.SaveChangesAsync();
                return new FiscaliseResult { Receipt = row };
            }

            int code = (int)response.StatusCode;
            bool outcomeUnknown = code >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout || code == 429;
            if (outcomeUnknown)
            {
                return FiscaliseResult.Problem("ZIMRA is not available right now",
                    $"Receipt {row.InvoiceNo} was signed and saved. It will be sent again automatically the next time you relay an invoice.",
                    response.DescribeError());
            }

            // FDMS refused the request outright. On a retry it may be refusing a receipt it already holds.
            if (isRetry)
            {
                var (status, _) = await GetStatusAsync(client);
                if (status != null && status.LastFiscalDayNo == row.FiscalDayNo && status.LastReceiptGlobalNo >= row.ReceiptGlobalNo)
                {
                    MarkRecorded(device, row, null, response.ResponseContent);
                    await _db.SaveChangesAsync();
                    return new FiscaliseResult { Receipt = row };
                }
            }

            _db.Receipts.Remove(row);
            await _db.SaveChangesAsync();
            _logger.LogWarning("SubmitReceipt refused for device {DeviceID}: {Error}", device.DeviceID, response.DescribeError());

            return FiscaliseResult.Problem("ZIMRA refused the receipt",
                "Nothing was recorded, so you can correct the invoice and relay it again.",
                response.DescribeError());
        }

        /// <summary>The receipt is part of the FDMS chain: advance the device's position together with the receipt status.</summary>
        private static void MarkRecorded(Device device, FiscalReceipt row, SubmitReceiptResponse? response, string rawResponse)
        {
            bool blocking = response?.ValidationErrors?.Any(e =>
                string.Equals(e.ValidationErrorColor, "Red", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(e.ValidationErrorColor, "Grey", StringComparison.OrdinalIgnoreCase)) ?? false;

            row.Status = blocking ? ReceiptStatus.Rejected : ReceiptStatus.Approved;
            row.FdmsReceiptId = response?.ReceiptID;
            row.ResponseJson = rawResponse;
            row.CompletedUtc = DateTime.UtcNow;

            device.LastReceiptCounter = row.ReceiptCounter;
            device.LastReceiptGlobalNo = row.ReceiptGlobalNo;
            device.LastReceiptHash = row.Hash;
            device.UpdatedUtc = DateTime.UtcNow;
        }

        private async Task<FiscaliseResult?> ResolvePendingAsync(Device device, IFdmsClient client)
        {
            var pending = await _db.Receipts
                .Where(r => r.DeviceRowId == device.Id && r.Status == ReceiptStatus.Pending)
                .OrderBy(r => r.Id)
                .ToListAsync();

            foreach (var row in pending)
            {
                var result = await SubmitAsync(device, client, row, isRetry: true);
                if (!result.Success && _db.Entry(row).State != EntityState.Detached)
                {
                    // Still pending: nothing else may be numbered until this one is settled.
                    return result;
                }
            }

            return null;
        }

        // ---------------------------------------------------------------------------------------
        // Fiscal day
        // ---------------------------------------------------------------------------------------

        private static async Task<(GetStatusResponse? status, FiscaliseResult? problem)> GetStatusAsync(IFdmsClient client)
        {
            var response = await client.GetStatusAsync();
            if (!response.IsSuccess)
            {
                return (null, FiscaliseResult.Problem("ZIMRA did not accept the device", response.DescribeError()));
            }
            return (response.GetContentAs<GetStatusResponse>(), null);
        }

        private async Task<FiscaliseResult?> EnsureDayOpenAsync(Device device, IFdmsClient client, GetStatusResponse status)
        {
            if (status.FiscalDayStatus == FiscalDayStatus.FiscalDayCloseInitiated)
            {
                return FiscaliseResult.Problem("The fiscal day is being closed",
                    "ZIMRA is still closing the previous fiscal day. Relay the invoice again in a minute.");
            }

            bool openAtFdms = status.FiscalDayStatus is FiscalDayStatus.FiscalDayOpened or FiscalDayStatus.FiscalDayCloseFailed;

            if (openAtFdms)
            {
                int fdmsDayNo = status.LastFiscalDayNo ?? 0;
                if (device.FiscalDayNo != fdmsDayNo || string.IsNullOrEmpty(device.FiscalDayOpened))
                {
                    return FiscaliseResult.Problem("Fiscal day mismatch",
                        $"ZIMRA has fiscal day {fdmsDayNo} open for this device, but this server has no record of it, so it cannot continue or close that day.",
                        "Close the fiscal day manually in the ZIMRA FDMS portal, then relay the invoice again. A new day will be opened automatically.");
                }

                device.FiscalDayStatus = status.FiscalDayStatus;

                // The day has run past the maximum length ZIMRA allows: close it and start a new one.
                var opened = TimeZoneHelper.ParseFdmsString(device.FiscalDayOpened)!.Value;
                int maxHours = DeviceStore.GetConfig(device)?.TaxPayerDayMaxHrs ?? DeviceStore.GetInfo(device).TaxPayerDayMaxHrs;
                if (maxHours > 0 && TimeZoneHelper.GetSouthAfricaTime() > opened.AddHours(maxHours))
                {
                    var close = await CloseDayCoreAsync(device, client);
                    if (!close.Success)
                    {
                        return FiscaliseResult.Problem("The previous fiscal day could not be closed",
                            $"Fiscal day {device.FiscalDayNo} has been open longer than the {maxHours} hours ZIMRA allows and has to be closed before new invoices can be sent.",
                            close.Message);
                    }
                    var (newStatus, problem) = await GetStatusAsync(client);
                    if (newStatus == null) return problem;
                    status = newStatus;
                }
                else
                {
                    await _db.SaveChangesAsync();
                    return null;
                }
            }

            // Closed at FDMS: open the next day.
            await RenewCertificateIfDueAsync(device);

            var request = new OpenDayRequest
            {
                FiscalDayNo = (status.LastFiscalDayNo ?? 0) + 1,
                FiscalDayOpened = TimeZoneHelper.ParseFdmsString(TimeZoneHelper.GetSouthAfricaTimeString())!.Value
            };

            // A renewed certificate needs a client that presents it.
            using var openClient = CreateClient(device);
            var response = await openClient.OpenDayAsync(request);
            if (!response.IsSuccess)
            {
                return FiscaliseResult.Problem("The fiscal day could not be opened", response.DescribeError());
            }

            var opened2 = response.GetContentAs<OpenDayResponse>();
            device.FiscalDayNo = opened2.FiscalDayNo > 0 ? opened2.FiscalDayNo : request.FiscalDayNo!.Value;
            device.FiscalDayOpened = TimeZoneHelper.ToFdmsString(request.FiscalDayOpened);
            device.FiscalDayStatus = FiscalDayStatus.FiscalDayOpened;
            device.LastReceiptCounter = 0;
            device.LastReceiptHash = string.Empty;
            device.CarriedSummaryJson = null;
            device.CarriedSummaryFiscalDayNo = 0;
            device.LastCloseDayError = null;
            device.UpdatedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            _logger.LogInformation("Opened fiscal day {DayNo} for device {DeviceID}", device.FiscalDayNo, device.DeviceID);

            // ZIMRA expects the configuration to be refreshed whenever a day is opened.
            await EnsureConfigAsync(device, openClient, force: true);
            return null;
        }

        /// <summary>Fetches the device configuration (taxes, day length, QR URL) when missing, stale or forced.</summary>
        public async Task<bool> EnsureConfigAsync(Device device, IFdmsClient client, bool force)
        {
            bool fresh = device.ConfigJson != null && device.ConfigFetchedUtc.HasValue &&
                         DateTime.UtcNow - device.ConfigFetchedUtc.Value < TimeSpan.FromHours(24);
            if (fresh && !force) return true;

            try
            {
                var response = await client.GetConfigAsync();
                if (!response.IsSuccess)
                {
                    _logger.LogWarning("GetConfig failed for device {DeviceID}: {Error}", device.DeviceID, response.DescribeError());
                    return device.ConfigJson != null;
                }

                var config = response.GetContentAs<GetConfigResponse>();
                device.ConfigJson = JsonConvert.SerializeObject(config);
                device.ConfigFetchedUtc = DateTime.UtcNow;
                if (config.CertificateValidTill != default) device.CertificateValidTillUtc = config.CertificateValidTill.UtcDateTime;
                await _db.SaveChangesAsync();
                return true;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _logger.LogWarning(ex, "GetConfig unreachable for device {DeviceID}", device.DeviceID);
                return device.ConfigJson != null;
            }
        }

        /// <summary>
        /// Requests a new certificate for the existing key shortly before the current one expires.
        /// Failure is logged and does not stop fiscalisation while the current certificate is still valid.
        /// </summary>
        private async Task RenewCertificateIfDueAsync(Device device)
        {
            if (device.CertificateValidTillUtc == null || string.IsNullOrEmpty(device.GeneratedCsr)) return;
            if (device.CertificateValidTillUtc.Value - DateTime.UtcNow > TimeSpan.FromDays(_options.CertificateRenewalDays)) return;

            try
            {
                using var client = CreateClient(device);
                var response = await client.IssueCertificateAsync(new IssueCertificateRequest { CertificateRequest = ToPem(device.GeneratedCsr) });
                if (!response.IsSuccess)
                {
                    _logger.LogWarning("Certificate renewal failed for device {DeviceID}: {Error}", device.DeviceID, response.DescribeError());
                    return;
                }

                string certificate = Utilities.CleanBase64String(response.GetContentAs<IssueCertificateResponse>().Certificate);
                string privateKey = _store.Unprotect(device.ProtectedPrivateKey);
                device.ProtectedPfx = _store.Protect(Utilities.GeneratePfx(privateKey, certificate, null));
                device.DeviceCertificate = certificate;
                using var parsed = RSA_CryptoHelper.ConvertCertificateFromBase64(certificate);
                device.CertificateValidTillUtc = parsed.NotAfter.ToUniversalTime();
                device.UpdatedUtc = DateTime.UtcNow;
                await _db.SaveChangesAsync();
                _logger.LogInformation("Renewed certificate for device {DeviceID}, valid till {ValidTill:u}", device.DeviceID, device.CertificateValidTillUtc);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Certificate renewal failed for device {DeviceID}", device.DeviceID);
            }
        }

        private static string ToPem(string csr)
        {
            if (csr.Contains("-----BEGIN")) return csr;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("-----BEGIN CERTIFICATE REQUEST-----");
            for (int i = 0; i < csr.Length; i += 64) sb.AppendLine(csr.Substring(i, Math.Min(64, csr.Length - i)));
            sb.AppendLine("-----END CERTIFICATE REQUEST-----");
            return sb.ToString();
        }

        /// <summary>Closes the open fiscal day of a device. Used by the Close Day button and the nightly job.</summary>
        public async Task<DayResult> CloseDayAsync(int deviceRowId)
        {
            using var deviceLock = await _locks.AcquireAsync(deviceRowId);
            var device = await _db.Devices.FirstAsync(d => d.Id == deviceRowId);
            using var client = CreateClient(device);

            try
            {
                var pendingProblem = await ResolvePendingAsync(device, client);
                if (pendingProblem != null)
                {
                    return new DayResult { Message = "A receipt is still waiting for ZIMRA's answer, so the day cannot be closed yet. " + string.Join(" ", pendingProblem.Problems) };
                }

                var (status, problem) = await GetStatusAsync(client);
                if (status == null) return new DayResult { Message = string.Join(" ", problem!.Problems) };

                if (status.FiscalDayStatus == FiscalDayStatus.FiscalDayClosed)
                {
                    device.FiscalDayStatus = FiscalDayStatus.FiscalDayClosed;
                    device.LastCloseDayError = null;
                    await _db.SaveChangesAsync();
                    return new DayResult { Success = true, Message = "The fiscal day is already closed." };
                }

                if (status.FiscalDayStatus == FiscalDayStatus.FiscalDayCloseInitiated)
                {
                    return await WaitForCloseAsync(device, client);
                }

                if ((status.LastFiscalDayNo ?? 0) != device.FiscalDayNo || string.IsNullOrEmpty(device.FiscalDayOpened))
                {
                    return new DayResult { Message = $"ZIMRA has fiscal day {status.LastFiscalDayNo} open but this server has no record of it. Close it manually in the ZIMRA FDMS portal." };
                }

                return await CloseDayCoreAsync(device, client);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return new DayResult { Message = "ZIMRA could not be reached: " + ex.Message };
            }
        }

        public async Task<List<FiscalDayCounter>> BuildDayCountersAsync(Device device)
        {
            var rows = await _db.Receipts.AsNoTracking()
                .Where(r => r.DeviceRowId == device.Id && r.FiscalDayNo == device.FiscalDayNo && r.Status != ReceiptStatus.Pending)
                .ToListAsync();

            var summaries = rows.Select(r => (FiscalDaySummary?)FiscalDayCounters.Summarise(ParseReceipt(r), device.FiscalDayNo)).ToList();

            if (!string.IsNullOrEmpty(device.CarriedSummaryJson) && device.CarriedSummaryFiscalDayNo == device.FiscalDayNo)
            {
                summaries.Add(JsonConvert.DeserializeObject<FiscalDaySummary>(device.CarriedSummaryJson));
            }

            return FiscalDayCounters.ToCounters(FiscalDayCounters.Combine(summaries));
        }

        private async Task<DayResult> CloseDayCoreAsync(Device device, IFdmsClient client)
        {
            var counters = await BuildDayCountersAsync(device);
            var opened = TimeZoneHelper.ParseFdmsString(device.FiscalDayOpened)!.Value;

            string source = ReceiptSigner.FiscalDayHashSource(device.DeviceID, device.FiscalDayNo, opened, counters);

            var request = new CloseDayRequest
            {
                FiscalDayNo = device.FiscalDayNo,
                FiscalDayCounters = counters,
                FiscalDayDeviceSignature = ReceiptSigner.Sign(_store.Unprotect(device.ProtectedPrivateKey), source),
                ReceiptCounter = device.LastReceiptCounter
            };

            var response = await client.CloseDayAsync(request);
            if (!response.IsSuccess)
            {
                device.LastCloseDayError = response.DescribeError();
                await _db.SaveChangesAsync();
                return new DayResult { Message = device.LastCloseDayError };
            }

            device.FiscalDayStatus = FiscalDayStatus.FiscalDayCloseInitiated;
            await _db.SaveChangesAsync();

            return await WaitForCloseAsync(device, client);
        }

        /// <summary>FDMS closes a day in the background; poll until it reports the result.</summary>
        private async Task<DayResult> WaitForCloseAsync(Device device, IFdmsClient client)
        {
            for (int attempt = 0; attempt < _options.CloseDayPollAttempts; attempt++)
            {
                if (_options.CloseDayPollDelayMs > 0) await Task.Delay(_options.CloseDayPollDelayMs);

                var (status, _) = await GetStatusAsync(client);
                if (status == null) continue;

                device.FiscalDayStatus = status.FiscalDayStatus;

                if (status.FiscalDayStatus == FiscalDayStatus.FiscalDayClosed)
                {
                    device.LastCloseDayError = null;
                    device.UpdatedUtc = DateTime.UtcNow;
                    await _db.SaveChangesAsync();
                    _logger.LogInformation("Closed fiscal day {DayNo} for device {DeviceID}", device.FiscalDayNo, device.DeviceID);
                    return new DayResult { Success = true, Message = $"Fiscal day {device.FiscalDayNo} is closed." };
                }

                if (status.FiscalDayStatus == FiscalDayStatus.FiscalDayCloseFailed)
                {
                    device.LastCloseDayError = DescribeCloseError(status.FiscalDayClosingErrorCode);
                    device.UpdatedUtc = DateTime.UtcNow;
                    await _db.SaveChangesAsync();
                    return new DayResult { Message = device.LastCloseDayError };
                }
            }

            await _db.SaveChangesAsync();
            return new DayResult { Message = "ZIMRA is still closing the fiscal day. Check again in a minute." };
        }

        private static string DescribeCloseError(FiscalDayProcessingError? error) => error switch
        {
            FiscalDayProcessingError.ReceiptsWithValidationErrors =>
                "ZIMRA could not close the day because it contains receipts with validation errors. Correct them with credit notes, or close the day manually in the ZIMRA FDMS portal.",
            FiscalDayProcessingError.MissingReceipts =>
                "ZIMRA could not close the day because some receipts of the day never reached it.",
            FiscalDayProcessingError.CountersMismatch =>
                "ZIMRA could not close the day because its totals differ from the totals of this server.",
            FiscalDayProcessingError.BadCertificateSignature =>
                "ZIMRA could not close the day because it did not accept the day's signature.",
            null => "ZIMRA could not close the fiscal day.",
            _ => $"ZIMRA could not close the fiscal day ({error})."
        };
    }

    /// <summary>The original receipt a credit or debit note refers to, kept so it can be written back to Manager.</summary>
    public class ReceiptReferenceInfo
    {
        public int DeviceIDRef { get; set; }
        public string? DeviceSNRef { get; set; }
        public int FiscalDayNoRef { get; set; }
        public int ReceiptCounterRef { get; set; }
        public int ReceiptGlobalNoRef { get; set; }
        public string? ReceiptRefNo { get; set; }
        public string? ReceiptRefDate { get; set; }
    }
}
