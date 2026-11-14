using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Newtonsoft.Json;
using ZimraEGS.ApiClient.Enums;
using ZimraEGS.ApiClient.Helpers;
using ZimraEGS.ApiClient.Models;

namespace ZimraEGS.Tests;

/// <summary>
/// Stand-in for ZIMRA FDMS. It re-derives every hash from the submitted payload with its own code and
/// checks signature, numbering and chaining, so the tests fail if the service signs or numbers wrongly.
/// </summary>
public class FakeFdms : IFdmsClientFactory, IFdmsClient
{
    public const int DeviceId = 12345;

    public RSA Key { get; } = RSA.Create(2048);
    public string PrivateKeyBase64 { get; }
    public string CertificateBase64 { get; }
    public string PfxBase64 { get; }

    public FiscalDayStatus DayStatus = FiscalDayStatus.FiscalDayClosed;
    public int LastFiscalDayNo;
    public int LastReceiptGlobalNo;
    public string DayOpened = "";
    public List<Receipt> DayReceipts = [];
    public List<Receipt> AllReceipts = [];
    public List<string> Violations = [];
    public int OpenDayCalls, CloseDayCalls, SubmitCalls;

    /// <summary>Drop the connection on the next submit. When true the receipt is recorded first (answer lost).</summary>
    public bool? FailNextSubmitAfterRecording;
    public HttpStatusCode? NextSubmitStatus;
    public List<ValidationError>? NextValidationErrors;
    public FiscalDayProcessingError? CloseDayError;

    /// <summary>How far ZIMRA's clock is ahead of this machine's.</summary>
    public TimeSpan ClockAhead = TimeSpan.Zero;
    public DateTimeOffset DayOpenedAt;

    public FakeFdms()
    {
        PrivateKeyBase64 = Convert.ToBase64String(Key.ExportPkcs8PrivateKey());
        var request = new CertificateRequest("CN=ZIMRA-TEST-0000012345", Key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        CertificateBase64 = Convert.ToBase64String(cert.Export(X509ContentType.Cert));
        PfxBase64 = Convert.ToBase64String(cert.Export(X509ContentType.Pfx));
    }

    public IFdmsClient CreateDeviceClient(PlatformType environment, int deviceID, string modelName, string modelVersion, string pfxBase64)
    {
        Assert.Equal(DeviceId, deviceID);
        Assert.Equal(PfxBase64, pfxBase64);
        return this;
    }

    public ApiHelper CreatePublicClient(PlatformType environment) => throw new NotSupportedException();

    public void Dispose() { }

    private ServerResponse Ok(object body) => new("fake", HttpStatusCode.OK, JsonConvert.SerializeObject(body)) { ServerTime = DateTimeOffset.UtcNow + ClockAhead };
    private static ServerResponse Fail(HttpStatusCode code, string errorCode, string title) =>
        new("fake", code, JsonConvert.SerializeObject(new { errorCode, title }));

    public Task<ServerResponse> GetStatusAsync() => Task.FromResult(Ok(new
    {
        operationID = "op",
        fiscalDayStatus = DayStatus.ToString(),
        lastReceiptGlobalNo = LastReceiptGlobalNo,
        lastFiscalDayNo = LastFiscalDayNo,
        fiscalDayClosingErrorCode = DayStatus == FiscalDayStatus.FiscalDayCloseFailed ? CloseDayError?.ToString() : null
    }));

    public Task<ServerResponse> GetConfigAsync() => Task.FromResult(Ok(new
    {
        operationID = "op",
        taxPayerName = "Test Trader",
        taxPayerTIN = "1234567890",
        vatNumber = "123456789",
        deviceSerialNo = "SN-1",
        deviceBranchName = "Main",
        deviceBranchAddress = new { province = "Harare", city = "Harare", street = "First", houseNo = "1" },
        deviceOperatingMode = "Online",
        taxPayerDayMaxHrs = 24,
        applicableTaxes = new object[]
        {
            new { taxName = "Exempt", validFrom = "2023-01-01T00:00:00", taxID = 1 },
            new { taxPercent = 0.0, taxName = "Zero rated 0%", validFrom = "2023-01-01T00:00:00", taxID = 2 },
            new { taxPercent = 15.0, taxName = "Standard rated 15%", validFrom = "2023-01-01T00:00:00", taxID = 3 },
        },
        certificateValidTill = "2030-01-01T00:00:00",
        qrUrl = "https://fdmstest.zimra.co.zw",
        taxpayerDayEndNotificationHrs = 2
    }));

    public Task<ServerResponse> OpenDayAsync(OpenDayRequest request)
    {
        OpenDayCalls++;
        if (DayStatus != FiscalDayStatus.FiscalDayClosed) return Task.FromResult(Fail(HttpStatusCode.BadRequest, "FISC01", "Fiscal day is not closed"));
        if (request.FiscalDayNo != LastFiscalDayNo + 1) return Task.FromResult(Fail(HttpStatusCode.BadRequest, "FISC02", "Wrong fiscal day number"));

        LastFiscalDayNo++;
        DayStatus = FiscalDayStatus.FiscalDayOpened;
        DayOpened = request.FiscalDayOpened.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        DayOpenedAt = request.FiscalDayOpened;
        // Like ZIMRA, compare against the server's own clock.
        if ((request.FiscalDayOpened - (DateTimeOffset.UtcNow + ClockAhead)).Duration() > TimeSpan.FromMinutes(5)) Violations.Add("fiscal day opening time is not ZIMRA's time");
        DayReceipts = [];
        return Task.FromResult(Ok(new { operationID = "op", fiscalDayNo = LastFiscalDayNo }));
    }

    private static string Cents(decimal v) => ((long)Math.Round(v * 100m, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);
    private static string Pct(decimal? p) => p == null ? "" : p.Value.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>Receipt hash input written independently of the production code, straight from the payload.</summary>
    public static string ExpectedReceiptSource(Receipt r, string? previousHash)
    {
        var sb = new StringBuilder();
        sb.Append(DeviceId).Append(r.ReceiptType.ToString().ToUpperInvariant()).Append(r.ReceiptCurrency).Append(r.ReceiptGlobalNo);
        sb.Append(r.ReceiptDate.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
        sb.Append(Cents(r.ReceiptTotal));
        foreach (var t in r.ReceiptTaxes.OrderBy(t => t.TaxID))
        {
            sb.Append(Pct(t.TaxPercent)).Append(Cents(t.TaxAmount)).Append(Cents(t.SalesAmountWithTax));
        }
        if (previousHash != null) sb.Append(previousHash);
        return sb.ToString();
    }

    private bool SignatureValid(string source, SignatureData signature)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(source));
        if (!hash.SequenceEqual(signature.Hash)) return false;
        return Key.VerifyHash(hash, signature.Signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    public Task<ServerResponse> SubmitReceiptAsync(SubmitReceiptRequest request)
    {
        SubmitCalls++;

        if (FailNextSubmitAfterRecording == false)
        {
            FailNextSubmitAfterRecording = null;
            throw new HttpRequestException("connection reset");
        }
        if (NextSubmitStatus is { } status)
        {
            NextSubmitStatus = null;
            return Task.FromResult(Fail(status, "RCPT500", "Refused"));
        }

        // The request body as it would travel over the wire.
        var r = JsonConvert.DeserializeObject<SubmitReceiptRequest>(JsonConvert.SerializeObject(request))!.Receipt;

        if (DayStatus != FiscalDayStatus.FiscalDayOpened) return Task.FromResult(Fail(HttpStatusCode.BadRequest, "RCPT01", "Fiscal day is not open"));

        // A receipt FDMS already holds is acknowledged again instead of being recorded twice.
        var duplicate = AllReceipts.FirstOrDefault(x => x.ReceiptGlobalNo == r.ReceiptGlobalNo &&
            x.ReceiptDeviceSignature.Hash.SequenceEqual(r.ReceiptDeviceSignature.Hash));

        if (duplicate == null)
        {
            var previous = DayReceipts.LastOrDefault();
            if (r.ReceiptCounter != DayReceipts.Count + 1) Violations.Add($"RCPT011 counter {r.ReceiptCounter}, expected {DayReceipts.Count + 1}");
            if (r.ReceiptGlobalNo != LastReceiptGlobalNo + 1) Violations.Add($"RCPT012 global number {r.ReceiptGlobalNo}, expected {LastReceiptGlobalNo + 1}");
            string? previousHash = previous == null ? null : Convert.ToBase64String(previous.ReceiptDeviceSignature.Hash);
            if (!SignatureValid(ExpectedReceiptSource(r, previousHash), r.ReceiptDeviceSignature)) Violations.Add("RCPT020 invalid signature");
            if (r.ReceiptTaxes.Sum(t => t.SalesAmountWithTax) != r.ReceiptTotal) Violations.Add("RCPT019 total");
            if (AllReceipts.Any(x => x.InvoiceNo == r.InvoiceNo)) Violations.Add("RCPT013 invoice number is not unique");
            if (r.ReceiptDate < DayOpenedAt) Violations.Add("RCPT014 receipt date earlier than fiscal day opening");
            if (r.ReceiptPayments.Sum(p => p.PaymentAmount) != r.ReceiptTotal) Violations.Add("RCPT039 payments");

            DayReceipts.Add(r);
            AllReceipts.Add(r);
            LastReceiptGlobalNo = r.ReceiptGlobalNo;
        }

        if (FailNextSubmitAfterRecording == true)
        {
            FailNextSubmitAfterRecording = null;
            throw new TaskCanceledException("timeout");
        }

        var errors = NextValidationErrors;
        NextValidationErrors = null;

        return Task.FromResult(Ok(new
        {
            operationID = "op",
            receiptID = 1000 + AllReceipts.FindIndex(x => x.ReceiptGlobalNo == r.ReceiptGlobalNo),
            serverDate = "2026-01-01T10:00:00",
            receiptServerSignature = new { hash = new byte[] { 1 }, signature = new byte[] { 2 }, certificateThumbprint = "AA" },
            validationErrors = errors
        }));
    }

    /// <summary>Day counters computed independently from the receipts FDMS holds for the day.</summary>
    public List<(string type, string currency, string detail, decimal value)> ExpectedCounters()
    {
        var list = new List<(int order, string type, string currency, int id, string detail, decimal value)>();
        foreach (var g in DayReceipts.SelectMany(r => r.ReceiptTaxes.Select(t => (r, t))).GroupBy(x => (x.r.ReceiptType, x.r.ReceiptCurrency, x.t.TaxID, x.t.TaxPercent)))
        {
            int baseOrder = g.Key.ReceiptType switch { ReceiptType.FiscalInvoice => 0, ReceiptType.CreditNote => 2, _ => 4 };
            string name = g.Key.ReceiptType switch { ReceiptType.FiscalInvoice => "Sale", ReceiptType.CreditNote => "CreditNote", _ => "DebitNote" };
            list.Add((baseOrder, name + "ByTax", g.Key.ReceiptCurrency, g.Key.TaxID, Pct(g.Key.TaxPercent), g.Sum(x => x.t.SalesAmountWithTax)));
            list.Add((baseOrder + 1, name + "TaxByTax", g.Key.ReceiptCurrency, g.Key.TaxID, Pct(g.Key.TaxPercent), g.Sum(x => x.t.TaxAmount)));
        }
        foreach (var g in DayReceipts.SelectMany(r => r.ReceiptPayments.Select(p => (r, p))).GroupBy(x => (x.r.ReceiptCurrency, x.p.MoneyTypeCode)))
        {
            list.Add((6, "BalanceByMoneyType", g.Key.ReceiptCurrency, (int)g.Key.MoneyTypeCode, g.Key.MoneyTypeCode.ToString(), g.Sum(x => x.p.PaymentAmount)));
        }
        return list.Where(c => c.value != 0)
            .OrderBy(c => c.order).ThenBy(c => c.currency, StringComparer.Ordinal).ThenBy(c => c.id)
            .Select(c => (c.type, c.currency, c.detail, c.value)).ToList();
    }

    public Task<ServerResponse> CloseDayAsync(CloseDayRequest request)
    {
        CloseDayCalls++;
        if (DayStatus is not (FiscalDayStatus.FiscalDayOpened or FiscalDayStatus.FiscalDayCloseFailed))
            return Task.FromResult(Fail(HttpStatusCode.BadRequest, "FISC04", "Fiscal day is not open"));

        var body = JsonConvert.DeserializeObject<CloseDayRequest>(JsonConvert.SerializeObject(request))!;
        var expected = ExpectedCounters();

        var sb = new StringBuilder();
        sb.Append(DeviceId).Append(LastFiscalDayNo).Append(DayOpened);
        foreach (var c in expected) sb.Append(c.type.ToUpperInvariant()).Append(c.currency).Append(c.detail.ToUpperInvariant()).Append(Cents(c.value));

        var sent = body.FiscalDayCounters.Select(c => (c.FiscalCounterType.ToString(), c.FiscalCounterCurrency,
            c.FiscalCounterType == FiscalCounterType.BalanceByMoneyType ? c.FiscalCounterMoneyType.ToString()! : Pct(c.FiscalCounterTaxPercent),
            c.FiscalCounterValue)).ToList();

        if (CloseDayError != null)
        {
            DayStatus = FiscalDayStatus.FiscalDayCloseFailed;
        }
        else if (body.FiscalDayNo != LastFiscalDayNo || body.ReceiptCounter != DayReceipts.Count || !sent.SequenceEqual(expected))
        {
            Violations.Add("CountersMismatch");
            CloseDayError = FiscalDayProcessingError.CountersMismatch;
            DayStatus = FiscalDayStatus.FiscalDayCloseFailed;
        }
        else if (!SignatureValid(sb.ToString(), body.FiscalDayDeviceSignature))
        {
            Violations.Add("BadCertificateSignature");
            CloseDayError = FiscalDayProcessingError.BadCertificateSignature;
            DayStatus = FiscalDayStatus.FiscalDayCloseFailed;
        }
        else
        {
            DayStatus = FiscalDayStatus.FiscalDayClosed;
        }

        return Task.FromResult(Ok(new { operationID = "op" }));
    }

    public Task<ServerResponse> IssueCertificateAsync(IssueCertificateRequest request) => throw new NotSupportedException();
}
