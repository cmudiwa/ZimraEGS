using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ZimraEGS.ApiClient.Enums;
using ZimraEGS.ApiClient.Models;
using ZimraEGS.Data;
using ZimraEGS.Helpers;
using ZimraEGS.Models;
using ZimraEGS.Services;

namespace ZimraEGS.Tests;

public class FiscalFlowTests : IDisposable
{
    private const string TaxStandard = "Standard rated 15% — 3 — C";
    private const string TaxZero = "Zero rated 0% — 2 — B";

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly FakeFdms _fdms = new();
    private readonly IDataProtectionProvider _protection = new EphemeralDataProtectionProvider();
    private readonly DeviceLocks _locks = new();

    public FiscalFlowTests()
    {
        _connection.Open();
        using var db = NewDb();
        db.Database.Migrate();
    }

    public void Dispose() => _connection.Dispose();

    private FiscalDbContext NewDb() => new(new DbContextOptionsBuilder<FiscalDbContext>().UseSqlite(_connection).Options);

    /// <summary>A fresh scope per request, as in the web application.</summary>
    private (FiscalService service, DeviceStore store, FiscalDbContext db) Scope()
    {
        var db = NewDb();
        var store = new DeviceStore(db, _protection, NullLogger<DeviceStore>.Instance);
        var options = Options.Create(new FiscalServiceOptions { CloseDayPollDelayMs = 0, CloseDayPollAttempts = 2 });
        return (new FiscalService(db, store, _fdms, _locks, options, NullLogger<FiscalService>.Instance), store, db);
    }

    private CertificateInfo LegacyCertificateInfo() => new()
    {
        IntegrationType = PlatformType.Simulation,
        DeviceID = FakeFdms.DeviceId,
        DeviceSerialNumber = "SN-1",
        PrivateKey = _fdms.PrivateKeyBase64,
        DeviceCertificate = _fdms.CertificateBase64,
        Base64Pfx = _fdms.PfxBase64,
        VatNumber = "123456789",
        QrUrl = "https://fdmstest.zimra.co.zw",
        TaxPayerDayMaxHrs = 24,
    };

    /// <summary>Registers the device the way the setup wizard does and returns the link Manager would store.</summary>
    private async Task<string> RegisterDeviceAsync()
    {
        var (_, store, db) = Scope();
        var device = new Device { DeviceID = FakeFdms.DeviceId, Environment = PlatformType.Simulation, IsActive = true };
        store.ApplyCertificateInfo(device, LegacyCertificateInfo());
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return await store.IssueLinkAsync(device);
    }

    private record LineSpec(string Name, decimal Qty, decimal Price, string? TaxName, decimal Rate, decimal Discount = 0, string Hs = "12345678");

    /// <summary>Builds the form Manager posts to the relay URL for one invoice or credit note.</summary>
    private static Dictionary<string, string> RelayForm(string deviceField, string formKey, IEnumerable<LineSpec> lines,
        string? paymentType = "0 - Cash", decimal? paymentAmount = null, bool creditNote = false, JObject? refInvoice = null,
        JObject? businessStrings = null, bool withBuyer = true)
    {
        string customerKey = Guid.NewGuid().ToString();
        var data = new JObject();

        var strings = businessStrings ?? new JObject();
        strings[ManagerCustomField.CertificateInfoGuid] = deviceField;
        data["BusinessDetails"] = new JObject { ["Name"] = "Test Trader", ["CustomFields2"] = new JObject { ["Strings"] = strings } };

        var customerStrings = new JObject();
        if (withBuyer)
        {
            customerStrings[ManagerCustomField.BuyerRegisterNameGuid] = "Buyer Ltd";
            customerStrings[ManagerCustomField.BuyerTINGuid] = "2000000001";
        }
        data[customerKey] = new JObject { ["Name"] = "Buyer", ["CustomFields2"] = new JObject { ["Strings"] = customerStrings } };

        var lineArray = new JArray();
        decimal total = 0;
        foreach (var line in lines)
        {
            string itemKey = Guid.NewGuid().ToString();
            data[itemKey] = new JObject
            {
                ["ItemName"] = line.Name,
                ["CustomFields2"] = new JObject { ["Strings"] = new JObject { [ManagerCustomField.HsCodeGuid] = line.Hs } }
            };

            var json = new JObject { ["Item"] = itemKey, ["Qty"] = line.Qty, ["SalesUnitPrice"] = line.Price };
            if (line.Discount != 0) json["DiscountAmount"] = line.Discount;
            if (line.TaxName != null)
            {
                string taxKey = Guid.NewGuid().ToString();
                data[taxKey] = new JObject { ["Name"] = line.TaxName, ["Rate"] = line.Rate };
                json["TaxCode"] = taxKey;
            }
            lineArray.Add(json);

            decimal net = line.Qty * line.Price - line.Discount;
            total += net + Math.Round(net * line.Rate / 100m, 2, MidpointRounding.AwayFromZero);
        }

        var invoiceStrings = new JObject();
        var invoiceDecimals = new JObject();
        if (paymentType != null)
        {
            invoiceStrings[ManagerCustomField.PaymentType1Guid] = paymentType;
            invoiceDecimals[ManagerCustomField.PaymentAmount1Guid] = paymentAmount ?? total;
        }
        if (creditNote) invoiceStrings[ManagerCustomField.ReceiptNotesGuid] = "Goods returned";

        var invoice = new JObject
        {
            ["IssueDate"] = "2026-01-01",
            ["Customer"] = customerKey,
            ["Lines"] = lineArray,
            ["CustomFields2"] = new JObject { ["Strings"] = invoiceStrings, ["Decimals"] = invoiceDecimals }
        };

        if (refInvoice != null)
        {
            string refKey = Guid.NewGuid().ToString();
            data[refKey] = refInvoice;
            invoice["SalesInvoice"] = refKey;
        }

        data[formKey] = invoice;

        return new Dictionary<string, string>
        {
            ["Referrer"] = $"http://localhost:55667/{(creditNote ? "credit-note-view" : "sales-invoice-view")}?abc",
            ["Key"] = formKey,
            ["Data"] = data.ToString(),
            ["Api"] = "http://localhost:55667/api2",
            ["Token"] = "manager-token",
            ["View"] = ""
        };
    }

    private async Task<(FiscaliseResult result, RelayData relay, Device device)> RelayAsync(Dictionary<string, string> form)
    {
        var (service, store, _) = Scope();
        var relay = new RelayData(form);
        var device = await store.ResolveAsync(relay);
        Assert.NotNull(device);
        var result = await service.FiscaliseAsync(device!.Id, relay);
        return (result, relay, device);
    }

    private static LineSpec[] SimpleLines() => [new("Widget", 2, 50m, TaxStandard, 15)];

    [Fact]
    public async Task One_relay_opens_the_day_and_fiscalises_the_invoice()
    {
        string link = await RegisterDeviceAsync();
        string key = Guid.NewGuid().ToString();

        var (result, relay, device) = await RelayAsync(RelayForm(link, key, SimpleLines()));

        Assert.True(result.Success, string.Join("; ", result.Problems));
        Assert.Equal(ReceiptStatus.Approved, result.Receipt!.Status);
        Assert.Equal(1, _fdms.OpenDayCalls);
        Assert.Equal("12345-0001-0001", result.Receipt.InvoiceNo);
        Assert.Equal(115.00m, result.Receipt.Total);
        Assert.Empty(_fdms.Violations);

        var sent = Assert.Single(_fdms.AllReceipts);
        Assert.Equal("Buyer Ltd", sent.BuyerData.BuyerRegisterName);
        // Empty contact and address blocks are left out; ZIMRA rejects a contact block with nothing in it.
        Assert.Null(sent.BuyerData.BuyerContacts);
        Assert.Null(sent.BuyerData.BuyerAddress);
        Assert.Equal(3, sent.ReceiptLines.Single().TaxID);
        Assert.Equal("12345678", sent.ReceiptLines.Single().ReceiptLineHSCode);

        // What the browser writes back into Manager.
        var call = ManagerWriteBack.Invoice(relay, device, result.Receipt);
        Assert.Equal($"http://localhost:55667/api2/sales-invoice-form/{key}", call.ApiUrl);
        var written = JObject.Parse(call.Payload);
        Assert.Equal("12345-0001-0001", (string?)written["Reference"]);
        Assert.Equal("APPROVED", (string?)written["CustomFields2"]!["Strings"]![ManagerCustomField.ApprovalStatusGuid]);
        Assert.StartsWith("https://fdmstest.zimra.co.zw/0000012345", (string?)written["CustomFields2"]!["Strings"]![ManagerCustomField.ReceiptQRCodeGuid]);
        Assert.Equal(1, (int)written["CustomFields2"]!["Decimals"]![ManagerCustomField.ReceiptGlobalNoGuid]!);
    }

    [Fact]
    public async Task Receipts_chain_and_the_day_closes_with_matching_totals()
    {
        string link = await RegisterDeviceAsync();

        var first = await RelayAsync(RelayForm(link, Guid.NewGuid().ToString(), SimpleLines()));
        var second = await RelayAsync(RelayForm(link, Guid.NewGuid().ToString(),
        [
            new("Widget", 3, 19.99m, TaxStandard, 15, Discount: 5m),
            new("Gadget", 1, 0.07m, TaxStandard, 15),
            new("Bread", 4, 1.25m, TaxZero, 0),
            new("Exempt service", 1, 10m, "Exempt — 1", 0),
        ], paymentType: "1 - Card"));
        var third = await RelayAsync(RelayForm(link, Guid.NewGuid().ToString(), SimpleLines(), withBuyer: false));

        Assert.True(first.result.Success && second.result.Success && third.result.Success,
            string.Join("; ", second.result.Problems.Concat(third.result.Problems)));
        Assert.Equal(3, third.result.Receipt!.ReceiptCounter);
        Assert.Equal(3, third.result.Receipt.ReceiptGlobalNo);
        Assert.Null(_fdms.AllReceipts[2].BuyerData);
        Assert.Equal(3, _fdms.AllReceipts[1].ReceiptTaxes.Count);
        Assert.Empty(_fdms.Violations);

        var (service, _, _) = Scope();
        var close = await service.CloseDayAsync(first.device.Id);

        Assert.True(close.Success, close.Message);
        Assert.Empty(_fdms.Violations);
        Assert.Equal(FiscalDayStatus.FiscalDayClosed, _fdms.DayStatus);

        // The next invoice starts a new day with the counter reset and the global number continuing.
        var next = await RelayAsync(RelayForm(link, Guid.NewGuid().ToString(), SimpleLines()));
        Assert.True(next.result.Success);
        Assert.Equal(2, _fdms.OpenDayCalls);
        Assert.Equal("12345-0002-0001", next.result.Receipt!.InvoiceNo);
        Assert.Equal(4, next.result.Receipt.ReceiptGlobalNo);
        Assert.Empty(_fdms.Violations);
    }

    [Fact]
    public async Task Relaying_the_same_invoice_again_does_not_fiscalise_it_twice()
    {
        string link = await RegisterDeviceAsync();
        var form = RelayForm(link, Guid.NewGuid().ToString(), SimpleLines());

        var first = await RelayAsync(form);
        var again = await RelayAsync(form);

        Assert.True(again.result.Success);
        Assert.True(again.result.AlreadyFiscalised);
        Assert.Equal(first.result.Receipt!.Hash, again.result.Receipt!.Hash);
        Assert.Single(_fdms.AllReceipts);
    }

    [Theory]
    [InlineData(true)]   // ZIMRA recorded the receipt but the answer was lost
    [InlineData(false)]  // the request never arrived
    public async Task A_lost_connection_never_breaks_the_chain(bool recordedByFdms)
    {
        string link = await RegisterDeviceAsync();
        await RelayAsync(RelayForm(link, Guid.NewGuid().ToString(), SimpleLines()));

        var lostForm = RelayForm(link, Guid.NewGuid().ToString(), SimpleLines());
        _fdms.FailNextSubmitAfterRecording = recordedByFdms;
        var lost = await RelayAsync(lostForm);
        Assert.False(lost.result.Success);

        // Another invoice is relayed next: the unfinished receipt goes first, with the same number and hash.
        var other = await RelayAsync(RelayForm(link, Guid.NewGuid().ToString(), SimpleLines()));
        Assert.True(other.result.Success, string.Join("; ", other.result.Problems));
        Assert.Equal(3, other.result.Receipt!.ReceiptCounter);

        var recovered = await RelayAsync(lostForm);
        Assert.True(recovered.result.Success);
        Assert.Equal(2, recovered.result.Receipt!.ReceiptCounter);

        Assert.Equal(3, _fdms.AllReceipts.Count);
        Assert.Empty(_fdms.Violations);

        var (service, _, _) = Scope();
        Assert.True((await service.CloseDayAsync(other.device.Id)).Success);
        Assert.Empty(_fdms.Violations);
    }

    [Fact]
    public async Task A_refused_receipt_does_not_use_up_a_number()
    {
        string link = await RegisterDeviceAsync();
        var form = RelayForm(link, Guid.NewGuid().ToString(), SimpleLines());

        _fdms.NextSubmitStatus = System.Net.HttpStatusCode.UnprocessableEntity;
        var refused = await RelayAsync(form);
        Assert.False(refused.result.Success);
        Assert.Contains("RCPT500", string.Join(" ", refused.result.Problems));

        var retry = await RelayAsync(form);
        Assert.True(retry.result.Success);
        Assert.Equal(1, retry.result.Receipt!.ReceiptCounter);
        Assert.Empty(_fdms.Violations);
    }

    [Fact]
    public async Task Problems_are_reported_before_anything_is_sent()
    {
        string link = await RegisterDeviceAsync();

        var noPayment = await RelayAsync(RelayForm(link, Guid.NewGuid().ToString(), SimpleLines(), paymentType: null));
        var wrongAmount = await RelayAsync(RelayForm(link, Guid.NewGuid().ToString(), SimpleLines(), paymentAmount: 100m));
        var noHsCode = await RelayAsync(RelayForm(link, Guid.NewGuid().ToString(), [new("Widget", 1, 10m, TaxStandard, 15, Hs: "")]));
        var wrongRate = await RelayAsync(RelayForm(link, Guid.NewGuid().ToString(), [new("Widget", 1, 10m, TaxStandard, 14.5m)]));

        Assert.Contains("how this invoice is paid", string.Join(" ", noPayment.result.Problems));
        Assert.Contains("must be equal", string.Join(" ", wrongAmount.result.Problems));
        Assert.Contains("HS Code is missing", string.Join(" ", noHsCode.result.Problems));

        // HS codes have 4 or 8 digits, and a VAT payer's zero-rated or exempt line needs all 8.
        var badLength = await RelayAsync(RelayForm(link, Guid.NewGuid().ToString(), [new("Widget", 1, 10m, TaxStandard, 15, Hs: "252329")]));
        var shortOnZero = await RelayAsync(RelayForm(link, Guid.NewGuid().ToString(), [new("Bread", 1, 10m, TaxZero, 0, Hs: "1905")]));
        Assert.Contains("must have 4 or 8 digits", string.Join(" ", badLength.result.Problems));
        Assert.Contains("8-digit HS Code", string.Join(" ", shortOnZero.result.Problems));
        Assert.Contains("14.5%", string.Join(" ", wrongRate.result.Problems));

        // A VAT payer's line without a tax code is not silently treated as exempt.
        var noTaxCode = await RelayAsync(RelayForm(link, Guid.NewGuid().ToString(), [new("Widget", 1, 10m, null, 0)]));
        Assert.Contains("has no tax code", string.Join(" ", noTaxCode.result.Problems));
        Assert.Equal(0, _fdms.SubmitCalls);

        // Nothing was numbered: the first good invoice is still receipt 1.
        var good = await RelayAsync(RelayForm(link, Guid.NewGuid().ToString(), SimpleLines()));
        Assert.Equal(1, good.result.Receipt!.ReceiptCounter);
    }

    [Fact]
    public async Task Payment_is_filled_in_from_the_invoice_type_or_the_device_default()
    {
        string link = await RegisterDeviceAsync();

        // A payment type without an amount means the whole invoice was paid that way.
        var typeOnly = await RelayAsync(RelayForm(link, Guid.NewGuid().ToString(), SimpleLines(), paymentType: "1 | Card", paymentAmount: 0m));
        Assert.True(typeOnly.result.Success, string.Join("; ", typeOnly.result.Problems));
        var card = Assert.Single(_fdms.AllReceipts[0].ReceiptPayments);
        Assert.Equal((MoneyType.Card, 115.00m), (card.MoneyTypeCode, card.PaymentAmount));

        // Nothing on the invoice: the default chosen for the device is used.
        var (_, store, _) = Scope();
        await store.SetDefaultMoneyTypeAsync((await store.FindByLinkAsync(link))!, (int)MoneyType.BankTransfer);

        var nothing = await RelayAsync(RelayForm(link, Guid.NewGuid().ToString(), SimpleLines(), paymentType: null));
        Assert.True(nothing.result.Success, string.Join("; ", nothing.result.Problems));
        var transfer = Assert.Single(_fdms.AllReceipts[1].ReceiptPayments);
        Assert.Equal((MoneyType.BankTransfer, 115.00m), (transfer.MoneyTypeCode, transfer.PaymentAmount));

        // An explicit payment on the invoice still wins over the default.
        var explicitCash = await RelayAsync(RelayForm(link, Guid.NewGuid().ToString(), SimpleLines()));
        Assert.Equal(MoneyType.Cash, _fdms.AllReceipts[2].ReceiptPayments.Single().MoneyTypeCode);
        Assert.Empty(_fdms.Violations);
    }

    [Fact]
    public async Task Setup_creates_tax_codes_from_the_taxes_zimra_configured()
    {
        string link = await RegisterDeviceAsync();
        await RelayAsync(RelayForm(link, Guid.NewGuid().ToString(), SimpleLines()));   // fetches the device configuration

        var (_, store, _) = Scope();
        var device = (await store.FindByLinkAsync(link))!;
        var records = ManagerSetupData.Build(device);

        var taxCodes = (JArray)records["tax-code-form"]!;
        Assert.Equal(["Exempt — 1", "Zero rated 0% — 2", "Standard rated 15% — 3"], taxCodes.Select(t => (string?)t["Name"]));
        Assert.Equal(15m, (decimal)taxCodes[2]["Rate"]!);
        Assert.Equal(ManagerSetupData.TaxCodeKey(3), (string?)taxCodes[2]["Key"]);

        // Fields that used to hold device state in Manager are no longer created.
        var fieldKeys = ((JArray)records["text-custom-field-form"]!).Select(f => (string?)f["Key"]).ToList();
        Assert.DoesNotContain(ManagerCustomField.BusinessReferenceGuid, fieldKeys);
        Assert.Contains(ManagerCustomField.CertificateInfoGuid, fieldKeys);
        Assert.NotNull(records["customer-form"]);

        // A live business gets no sample customers or items.
        device.Environment = PlatformType.Production;
        var live = ManagerSetupData.Build(device);
        Assert.Null(live["customer-form"]);
        Assert.Null(live["inventory-item-form"]);
        Assert.NotNull(live["theme-form"]);
    }

    [Fact]
    public async Task Tax_codes_without_an_id_are_matched_by_rate()
    {
        string link = await RegisterDeviceAsync();

        var (result, _, _) = await RelayAsync(RelayForm(link, Guid.NewGuid().ToString(),
        [
            new("Widget", 1, 100m, "VAT 15%", 15),
            new("Bread", 1, 10m, "Zero rated", 0),
        ]));

        Assert.True(result.Success, string.Join("; ", result.Problems));
        Assert.Equal([3, 2], _fdms.AllReceipts.Single().ReceiptLines.Select(l => l.TaxID));
        Assert.Empty(_fdms.Violations);
    }

    [Fact]
    public async Task Credit_note_refers_to_the_original_receipt_with_negative_amounts()
    {
        string link = await RegisterDeviceAsync();
        var invoiceForm = RelayForm(link, Guid.NewGuid().ToString(), SimpleLines());
        var invoice = await RelayAsync(invoiceForm);

        // The original invoice as Manager holds it after the write-back.
        var storedInvoice = JObject.Parse(ManagerWriteBack.Invoice(invoice.relay, invoice.device, invoice.result.Receipt!).Payload);

        var credit = await RelayAsync(RelayForm(link, Guid.NewGuid().ToString(), SimpleLines(), creditNote: true, refInvoice: storedInvoice));

        Assert.True(credit.result.Success, string.Join("; ", credit.result.Problems));
        var sent = _fdms.AllReceipts[1];
        Assert.Equal(ReceiptType.CreditNote, sent.ReceiptType);
        Assert.Equal(-115.00m, sent.ReceiptTotal);
        Assert.Equal(1000, sent.CreditDebitNote.ReceiptID);
        Assert.Equal(1, sent.CreditDebitNote.ReceiptGlobalNo);
        Assert.Empty(_fdms.Violations);

        var (service, _, _) = Scope();
        Assert.True((await service.CloseDayAsync(credit.device.Id)).Success);
        Assert.Empty(_fdms.Violations);
    }

    [Fact]
    public async Task Flagged_receipt_stays_in_the_chain_and_is_reported()
    {
        string link = await RegisterDeviceAsync();
        _fdms.NextValidationErrors = [new ValidationError { ValidationErrorCode = "RCPT026", ValidationErrorColor = "Red" }];

        var flagged = await RelayAsync(RelayForm(link, Guid.NewGuid().ToString(), SimpleLines()));
        var next = await RelayAsync(RelayForm(link, Guid.NewGuid().ToString(), SimpleLines()));

        Assert.True(flagged.result.Success);
        Assert.Equal(ReceiptStatus.Rejected, flagged.result.Receipt!.Status);
        Assert.Equal(2, next.result.Receipt!.ReceiptCounter);
        Assert.Empty(_fdms.Violations);
    }

    [Fact]
    public async Task Existing_business_with_keys_in_manager_is_imported_with_its_open_day()
    {
        // State of a business running the previous version: day 7 open, two receipts sent.
        var summary = new FiscalDaySummary
        {
            TaxSummaries = [new TaxSummary { FiscalDayNo = 7, ReceiptType = ReceiptType.FiscalInvoice, ReceiptCurrency = "ZWG", TaxID = 3, TaxPersen = "15.00", TaxAmount = 30m, SalesAmountWithTax = 230m }],
            PaymentSummaries = [new PaymentSummary { FiscalDayNo = 7, ReceiptCurrency = "ZWG", MoneyTypeCode = MoneyType.Cash, PaymentAmount = 230m }]
        };
        var previous = new Receipt
        {
            ReceiptType = ReceiptType.FiscalInvoice, ReceiptCurrency = "ZWG", ReceiptCounter = 2, ReceiptGlobalNo = 2,
            ReceiptTaxes = [new ReceiptTax { TaxID = 3, TaxPercent = 15m, TaxAmount = 30m, SalesAmountWithTax = 230m }],
            ReceiptPayments = [new Payment { MoneyTypeCode = MoneyType.Cash, PaymentAmount = 230m }],
            ReceiptTotal = 230m,
            ReceiptDeviceSignature = new SignatureData { Hash = [9, 9, 9], Signature = [1] }
        };
        _fdms.LastFiscalDayNo = 7;
        _fdms.DayStatus = FiscalDayStatus.FiscalDayOpened;
        _fdms.DayOpened = "2026-01-05";
        _fdms.LastReceiptGlobalNo = 2;
        _fdms.DayReceipts = [new Receipt { ReceiptDeviceSignature = new SignatureData { Hash = [0] } }, previous];
        _fdms.DayReceipts[0].ReceiptTaxes.Clear();

        var reference = new BusinessReference
        {
            DeviceID = FakeFdms.DeviceId, FiscalDayStatus = FiscalDayStatus.FiscalDayOpened,
            FiscalDayOpened = new DateTimeOffset(2026, 1, 5, 8, 0, 0, TimeSpan.FromHours(2)),
            LastFiscalDayNo = 7, LastReceiptCounter = 2, LastReceiptGlobalNo = 2, LastReceiptHash = Convert.ToBase64String([9, 9, 9])
        };
        var businessStrings = new JObject
        {
            [ManagerCustomField.BusinessReferenceGuid] = JsonConvert.SerializeObject(reference),
            [ManagerCustomField.FiscalDaySummaryGuid] = JsonConvert.SerializeObject(summary)
        };
        string blob = ObjectCompressor.SerializeToBase64String(LegacyCertificateInfo());

        // Day 7 was opened long ago, so it is closed and a new day opened as part of the same relay.
        var (result, relay, device) = await RelayAsync(RelayForm(blob, Guid.NewGuid().ToString(), SimpleLines(), businessStrings: businessStrings));

        Assert.True(result.Success, string.Join("; ", result.Problems));
        Assert.Empty(_fdms.Violations);
        Assert.Equal(1, _fdms.CloseDayCalls);
        Assert.Equal("12345-0008-0001", result.Receipt!.InvoiceNo);
        Assert.Equal(3, result.Receipt.ReceiptGlobalNo);

        // The write-back replaces the key blob in Manager with a link that works on the next relay.
        var (_, store, _) = Scope();
        string link = await store.IssueLinkAsync((await store.FindAsync(PlatformType.Simulation, FakeFdms.DeviceId))!);
        var business = JObject.Parse(ManagerWriteBack.BusinessLink(relay.Api, relay.BusinessDetailJson, link).Payload);
        var stored = (JObject)business["CustomFields2"]!["Strings"]!;
        Assert.Equal(link, (string?)stored[ManagerCustomField.CertificateInfoGuid]);
        Assert.DoesNotContain(_fdms.PrivateKeyBase64, business.ToString());
        Assert.Null(stored[ManagerCustomField.BusinessReferenceGuid]);

        var viaLink = await RelayAsync(RelayForm(link, Guid.NewGuid().ToString(), SimpleLines()));
        Assert.True(viaLink.result.Success);
        Assert.Equal(device.Id, viaLink.device.Id);
    }

    private static CertificateInfo LegacyCertificateInfoForAddress() => new()
    {
        DeviceBranchName = "Main", TaxPayerTIN = "1234567890", VatNumber = "123456789",
        HouseNo = "1", Street = "First", City = "Harare", Province = "Harare", Email = "a@b.co", PhoneNo = "077",
    };

    /// <summary>A document as the extension page sends it: read from Manager's API with references filled in.</summary>
    private static JObject ApiInvoice(JArray lines, bool discount = false, int discountType = 0, bool withBuyer = true, JObject? strings = null)
    {
        return new JObject
        {
            ["issueDate"] = "2026-11-13T00:00:00", ["dueDate"] = 0, ["dueDateDays"] = null, ["dueDateDate"] = null, ["reference"] = null,
            ["customer"] = new JObject
            {
                ["name"] = "Buyer", ["currency"] = null,
                ["customFields2"] = withBuyer
                    ? new JObject { ["strings"] = new JObject { [ManagerCustomField.BuyerRegisterNameGuid] = "Buyer Ltd", [ManagerCustomField.BuyerTINGuid] = "2000000001" }, ["decimals"] = null }
                    : null
            },
            ["lines"] = lines, ["discount"] = discount, ["discountType"] = discountType, ["amountsIncludeTax"] = false,
            ["customFields"] = null,
            ["customFields2"] = new JObject { ["strings"] = strings ?? new JObject { [ManagerCustomField.PaymentType1Guid] = "0 | Cash" }, ["decimals"] = new JObject(), ["dates"] = null }
        };
    }

    private static JObject ApiLine(string name, decimal? qty, decimal price, int taxId, decimal rate, decimal discountPercentage = 0, decimal discountAmount = 0)
    {
        return new JObject
        {
            ["item"] = new JObject { ["itemName"] = name, ["customFields2"] = new JObject { ["strings"] = new JObject { [ManagerCustomField.HsCodeGuid] = "12345678" } } },
            ["account"] = null, ["lineDescription"] = null, ["customFields2"] = null,
            ["qty"] = qty, ["salesUnitPrice"] = price, ["discountPercentage"] = discountPercentage, ["discountAmount"] = discountAmount,
            // Manager: taxRate 2 = custom rate, type 0 = single rate.
            ["taxCode"] = new JObject { ["name"] = $"Tax — {taxId}", ["taxRate"] = 2, ["type"] = 0, ["rate"] = rate, ["components"] = null }
        };
    }

    [Fact]
    public async Task Document_read_through_the_manager_extension_is_fiscalised_and_patched_back()
    {
        string link = await RegisterDeviceAsync();
        var business = new JObject { ["name"] = "Demo", ["customFields2"] = new JObject { ["strings"] = new JObject { [ManagerCustomField.CertificateInfoGuid] = link } } };
        string key = Guid.NewGuid().ToString();

        var document = ApiInvoice(
        [
            ApiLine("Widget", 4, 25m, 3, 15, discountPercentage: 10),   // 100 less 10% = 90, plus 15% tax
            ApiLine("Delivery", null, 8m, 2, 0),                        // an empty quantity means one
        ], discount: true, discountType: 0);

        var (service, store, _) = Scope();
        var relay = RelayData.FromExtension("/sales-invoice-view", key, document, business, "USD");
        var device = await store.ResolveAsync(relay);
        Assert.NotNull(device);

        Assert.Empty((await service.PreviewAsync(device!.Id, relay)).Problems);
        Assert.Equal(0, _fdms.SubmitCalls);

        var result = await service.FiscaliseAsync(device.Id, relay);

        Assert.True(result.Success, string.Join("; ", result.Problems));
        Assert.Empty(_fdms.Violations);
        var sent = Assert.Single(_fdms.AllReceipts);
        Assert.Equal("USD", sent.ReceiptCurrency);
        Assert.Equal(111.50m, sent.ReceiptTotal);
        Assert.Equal(3, sent.ReceiptLines.Count);   // two sales lines and one discount line
        Assert.Equal(1m, sent.ReceiptLines.ElementAt(1).ReceiptLineQuantity);
        Assert.Equal(-10m, sent.ReceiptLines.ElementAt(2).ReceiptLineTotal);

        // What the page sends back to Manager: a merge patch with only the changed fields.
        var patch = ManagerWriteBack.InvoicePatch(device, result.Receipt!);
        Assert.Equal("PATCH", patch.Method);
        Assert.True((bool)patch.Body!["customTheme"]!);
        Assert.Equal(ManagerSetupData.ThemeKey, (string?)patch.Body["customThemeId"]);
        Assert.Equal("FISCAL TAX INVOICE", (string?)patch.Body["customFields2"]!["strings"]![ManagerCustomField.FiscalDocumentGuid]);
        Assert.Equal("Buyer Ltd\nTIN: 2000000001", (string?)patch.Body["customFields2"]!["strings"]![ManagerCustomField.FiscalBuyerGuid]);

        // The lines as reported to ZIMRA travel with the document, so the printout shows the VAT on each line.
        var reported = JObject.Parse((string)patch.Body["customFields2"]!["strings"]![ManagerCustomField.FiscalLinesGuid]!);
        Assert.False((bool)reported["inclusive"]!);
        Assert.Equal(3, ((JArray)reported["lines"]!).Count);
        Assert.Equal(15m, (decimal)reported["lines"]![0]!["vat"]!);        // 15% of 100.00
        Assert.Equal(115m, (decimal)reported["lines"]![0]!["total"]!);
        Assert.Equal(-1.5m, (decimal)reported["lines"]![2]!["vat"]!);      // the discount line reduces the VAT
        Assert.Equal(111.50m, (decimal)reported["total"]!);
        Assert.Equal("Cash", (string?)reported["payments"]![0]!["method"]);
        Assert.Equal($"/api3/sales-invoice-form/{key}", patch.ApiUrl);
        Assert.Equal("12345-0001-0001", (string?)patch.Body!["reference"]);
        Assert.Equal("APPROVED", (string?)patch.Body["customFields2"]!["strings"]![ManagerCustomField.ApprovalStatusGuid]);
        Assert.Equal(1, (int)patch.Body["customFields2"]!["decimals"]![ManagerCustomField.ReceiptGlobalNoGuid]!);

        // A credit note against that invoice, with the original read back as Manager now holds it.
        var original = (JObject)document.DeepClone();
        original.Merge(patch.Body);
        var note = ApiInvoice([ApiLine("Widget", 1, 25m, 3, 15)],
            strings: new JObject { [ManagerCustomField.PaymentType1Guid] = "0 | Cash", [ManagerCustomField.ReceiptNotesGuid] = "Returned" });
        note["salesInvoice"] = original;

        var credit = await service.FiscaliseAsync(device.Id, RelayData.FromExtension("/credit-note-view", Guid.NewGuid().ToString(), note, business, "USD"));

        Assert.True(credit.Success, string.Join("; ", credit.Problems));
        Assert.Equal(ReceiptType.CreditNote, _fdms.AllReceipts[1].ReceiptType);
        Assert.Equal(-28.75m, _fdms.AllReceipts[1].ReceiptTotal);
        Assert.Equal(1, _fdms.AllReceipts[1].CreditDebitNote.ReceiptGlobalNo);
        Assert.StartsWith("/api3/credit-note-form/", ManagerWriteBack.InvoicePatch(device, credit.Receipt!).ApiUrl);
        Assert.Equal("CREDIT NOTE", (string?)ManagerWriteBack.InvoicePatch(device, credit.Receipt!).Body!["customFields2"]!["strings"]![ManagerCustomField.FiscalDocumentGuid]);
        Assert.Empty(_fdms.Violations);
    }

    [Fact]
    public async Task Setup_records_use_the_names_and_numbers_of_managers_api()
    {
        string link = await RegisterDeviceAsync();
        await RelayAsync(RelayForm(link, Guid.NewGuid().ToString(), SimpleLines()));

        var (_, store, _) = Scope();
        var calls = ManagerSetupData.BuildApiCalls((await store.FindByLinkAsync(link))!);

        var taxCode = calls.Single(c => c.ApiUrl == $"/api3/tax-code-form/{ManagerSetupData.TaxCodeKey(3)}");
        Assert.Equal("PUT", taxCode.Method);
        Assert.Equal("Standard rated 15% — 3", (string?)taxCode.Body!["name"]);
        Assert.Equal(2, (int)taxCode.Body["taxRate"]!);     // custom rate
        Assert.Equal(0, (int)taxCode.Body["type"]!);        // single rate

        var paymentType = calls.Single(c => c.ApiUrl.EndsWith(ManagerCustomField.PaymentType1Guid));
        Assert.StartsWith("/api3/text-custom-field-form/", paymentType.ApiUrl);
        Assert.Equal(2, (int)paymentType.Body!["type"]!);   // dropdown list
        Assert.Contains("0 | Cash", (string?)paymentType.Body["optionsForDropdownList"]);
        Assert.Null(paymentType.Body["Name"]);

        // The theme is a complete HTML page in ZIMRA's layout, and fiscalised documents are switched to it.
        var theme = calls.Single(c => c.ApiUrl == $"/api3/custom-theme-form/{ManagerSetupData.ThemeKey}");
        Assert.StartsWith("<!DOCTYPE html>", (string?)theme.Body!["template"]);
        Assert.Contains("FISCAL TAX INVOICE", (string?)theme.Body["template"]);

        // The device link is a secret: its field must not be shown on views or printed documents.
        var linkField = calls.Single(c => c.ApiUrl.EndsWith(ManagerCustomField.CertificateInfoGuid));
        Assert.False((bool)linkField.Body!["displayOnView"]!);

        // Sample items point at tax codes that exist for the device.
        var keys = calls.Where(c => c.ApiUrl.StartsWith("/api3/tax-code-form/")).Select(c => c.ApiUrl.Split('/').Last()).ToList();
        Assert.All(calls.Where(c => c.ApiUrl.StartsWith("/api3/inventory-item-form/")), c => Assert.Contains((string?)c.Body!["defaultTaxCode"], keys));

        var info = LegacyCertificateInfoForAddress();
        Assert.Equal("Main\nTIN: 1234567890   VAT No: 123456789\n1, First, Harare\na@b.co\n077", info.ToBusinessAddress());

        var businessPatch = ManagerWriteBack.BusinessLinkPatch(link, "Demo", "Address");
        Assert.Equal(link, (string?)businessPatch.Body!["customFields2"]!["strings"]![ManagerCustomField.CertificateInfoGuid]);
    }

    [Fact]
    public async Task Receipts_are_stamped_with_zimras_clock_not_the_local_one()
    {
        // This machine's clock is two hours behind ZIMRA's, for example through a wrong time zone setting.
        _fdms.ClockAhead = TimeSpan.FromHours(2);
        string link = await RegisterDeviceAsync();

        var (result, _, _) = await RelayAsync(RelayForm(link, Guid.NewGuid().ToString(), SimpleLines()));

        Assert.True(result.Success, string.Join("; ", result.Problems));
        var sent = Assert.Single(_fdms.AllReceipts);
        var zimraNow = DateTimeOffset.UtcNow + _fdms.ClockAhead;
        Assert.InRange((sent.ReceiptDate - zimraNow).Duration(), TimeSpan.Zero, TimeSpan.FromSeconds(30));
        Assert.True(sent.ReceiptDate >= _fdms.DayOpenedAt, "the receipt must not be dated before the fiscal day opened");
        Assert.Empty(_fdms.Violations);
    }

    [Fact]
    public async Task A_four_digit_hs_code_is_sent_as_entered_and_the_configuration_is_read_before_the_day_opens()
    {
        string link = await RegisterDeviceAsync();

        var (result, _, _) = await RelayAsync(RelayForm(link, Guid.NewGuid().ToString(), [new("Cement", 1, 10m, TaxStandard, 15, Hs: "2523")]));

        Assert.True(result.Success, string.Join("; ", result.Problems));
        Assert.Equal("2523", _fdms.AllReceipts.Single().ReceiptLines.Single().ReceiptLineHSCode);
        Assert.True(_fdms.ConfigReadBeforeOpenDay, "getConfig must be called before a new fiscal day is opened");
    }

    // ---------------- subscription

    private (FiscalService service, DeviceStore store, FiscalDbContext db, BillingService billing) BillingScope(BillingOptions? options = null)
    {
        var db = NewDb();
        var store = new DeviceStore(db, _protection, NullLogger<DeviceStore>.Instance);
        var billing = new BillingService(db, Options.Create(options ?? new BillingOptions()));
        var fiscalOptions = Options.Create(new FiscalServiceOptions { CloseDayPollDelayMs = 0, CloseDayPollAttempts = 2 });
        return (new FiscalService(db, store, _fdms, _locks, fiscalOptions, NullLogger<FiscalService>.Instance, billing), store, db, billing);
    }

    /// <summary>The test device, moved to the live environment and linked to a new account.</summary>
    private async Task<(string link, int deviceRowId)> LiveDeviceAsync(DateTime? paidUntilUtc, bool withAccount = true)
    {
        string link = await RegisterDeviceAsync();
        var (_, store, db, billing) = BillingScope();
        var device = (await store.FindByLinkAsync(link))!;
        device.Environment = PlatformType.Production;
        await db.SaveChangesAsync();
        if (withAccount)
        {
            var (account, _) = await billing.CreateAccountAsync("owner@example.com", "a long password", "Test Trader", "");
            await billing.LinkDeviceAsync(device, account!);
        }
        device.PaidUntilUtc = paidUntilUtc;
        await db.SaveChangesAsync();
        return ("ZEGS-LINK:Production:" + string.Join(':', link.Split(':').Skip(2)), device.Id);
    }

    private async Task<FiscaliseResult> SendAsync(string link)
    {
        var (service, store, _, _) = BillingScope();
        var relay = new RelayData(RelayForm(link, Guid.NewGuid().ToString(), SimpleLines()));
        var device = await store.ResolveAsync(relay);
        return await service.FiscaliseAsync(device!.Id, relay);
    }

    [Fact]
    public void Subscription_status_follows_the_paid_period()
    {
        var (_, _, _, billing) = BillingScope(new BillingOptions { GraceDays = 7, WarnDays = 7 });
        var now = new DateTime(2026, 11, 16, 10, 0, 0, DateTimeKind.Utc);
        Device Live(DateTime? until, int? account = 1) => new() { Environment = PlatformType.Production, AccountId = account, PaidUntilUtc = until };

        Assert.Equal(BillingState.Free, billing.StatusOf(new Device { Environment = PlatformType.Simulation }, now).State);
        Assert.Equal(BillingState.NoAccount, billing.StatusOf(Live(now.AddDays(30), null), now).State);
        Assert.Equal(BillingState.Active, billing.StatusOf(Live(now.AddDays(30)), now).State);
        Assert.Equal(BillingState.EndingSoon, billing.StatusOf(Live(now.AddDays(3)), now).State);
        Assert.Equal(BillingState.Grace, billing.StatusOf(Live(now.AddDays(-3)), now).State);
        Assert.Equal(BillingState.Expired, billing.StatusOf(Live(now.AddDays(-8)), now).State);
        Assert.Equal(BillingState.Expired, billing.StatusOf(Live(null), now).State);
        Assert.True(billing.StatusOf(Live(now.AddDays(-3)), now).MaySend);
        Assert.False(billing.StatusOf(Live(now.AddDays(-8)), now).MaySend);

        // Switched off: everything may send.
        var (_, _, _, off) = BillingScope(new BillingOptions { Enabled = false });
        Assert.True(off.StatusOf(Live(null, null), now).MaySend);
    }

    [Fact]
    public async Task A_recorded_payment_extends_from_the_end_or_from_today()
    {
        var (_, rowId) = await LiveDeviceAsync(paidUntilUtc: null);
        var (_, _, db, billing) = BillingScope();
        var now = new DateTime(2026, 11, 16, 10, 0, 0, DateTimeKind.Utc);

        // Ended long ago: the new period starts today, so the gap is not charged for.
        var device = await db.Devices.FirstAsync(d => d.Id == rowId);
        device.PaidUntilUtc = now.AddDays(-40); await db.SaveChangesAsync();
        var first = await billing.RecordPaymentAsync(rowId, 1, 0, 25m, "usd", "EcoCash MP1", "", "admin@example.com", now);
        Assert.Equal(now, first.PeriodFromUtc);
        Assert.Equal(now.AddMonths(1), first.PeriodToUtc);
        Assert.Equal("USD", first.Currency);

        // Still running: the next payment continues from its end.
        var second = await billing.RecordPaymentAsync(rowId, 0, 10, null, "", "", "goodwill", "admin@example.com", now.AddDays(5));
        Assert.Equal(now.AddMonths(1), second.PeriodFromUtc);
        Assert.Equal(now.AddMonths(1).AddDays(10), second.PeriodToUtc);

        await Assert.ThrowsAsync<ArgumentException>(() => billing.RecordPaymentAsync(rowId, 0, 0, null, "", "", "", "a", now));
    }

    [Fact]
    public async Task A_lapsed_subscription_stops_new_documents_but_never_the_fiscal_chain()
    {
        // Paid up: one document goes through and opens the day.
        var (link, rowId) = await LiveDeviceAsync(DateTime.UtcNow.AddDays(30));
        Assert.True((await SendAsync(link)).Success);

        // A second document is signed but ZIMRA's answer is lost; then the subscription runs out.
        _fdms.FailNextSubmitAfterRecording = true;
        Assert.False((await SendAsync(link)).Success);
        var (_, _, db, _) = BillingScope();
        (await db.Devices.FirstAsync(d => d.Id == rowId)).PaidUntilUtc = DateTime.UtcNow.AddDays(-30);
        await db.SaveChangesAsync();

        // A new document is refused, but the unanswered receipt is still sent first.
        var refused = await SendAsync(link);
        Assert.False(refused.Success);
        Assert.Equal("The subscription has ended", refused.ProblemTitle);
        Assert.Equal(2, _fdms.AllReceipts.Count);
        Assert.Equal(2, _fdms.DayReceipts.Count);

        // Closing the day is never blocked.
        var (service, _, _, _) = BillingScope();
        Assert.True((await service.CloseDayAsync(rowId)).Success);
        Assert.Empty(_fdms.Violations);

        // Recording a payment lets documents through again.
        var (_, _, _, billing) = BillingScope();
        await billing.RecordPaymentAsync(rowId, 1, 0, 25m, "USD", "cash", "", "admin@example.com");
        Assert.True((await SendAsync(link)).Success);
    }

    [Fact]
    public async Task A_live_device_without_an_account_cannot_send_and_a_new_one_gets_its_trial()
    {
        var (link, rowId) = await LiveDeviceAsync(paidUntilUtc: null, withAccount: false);
        var refused = await SendAsync(link);
        Assert.Equal("This device is not linked to an account", refused.ProblemTitle);
        Assert.Equal(0, _fdms.SubmitCalls);

        var (_, _, db, billing) = BillingScope(new BillingOptions { TrialDays = 14 });
        var (account, _) = await billing.CreateAccountAsync("Shop@Example.com ", "a long password", "Shop", "077");
        Assert.Equal("shop@example.com", account!.Email);
        var device = await db.Devices.FirstAsync(d => d.Id == rowId);
        await billing.LinkDeviceAsync(device, account);
        Assert.InRange((device.PaidUntilUtc!.Value - DateTime.UtcNow).TotalDays, 13.9, 14.1);
        Assert.True((await SendAsync(link)).Success);
    }

    [Fact]
    public async Task Sign_in_checks_the_password_and_locks_after_five_wrong_ones()
    {
        var (_, _, _, billing) = BillingScope();
        Assert.NotNull((await billing.CreateAccountAsync("a@b.co", "correct horse battery", "A", "")).account);
        Assert.Equal("There is already an account with this e-mail address. Sign in instead.", (await billing.CreateAccountAsync("A@B.CO", "another password", "B", "")).error);
        Assert.NotNull((await billing.CreateAccountAsync("c@d.co", "short", "C", "")).error);

        Assert.NotNull((await billing.AuthenticateAsync("A@b.co", "correct horse battery")).account);
        for (int i = 0; i < 5; i++) Assert.Null((await billing.AuthenticateAsync("a@b.co", "wrong")).account);
        var locked = await billing.AuthenticateAsync("a@b.co", "correct horse battery");
        Assert.Null(locked.account);
        Assert.Contains("Too many wrong passwords", locked.error);
        Assert.Null((await billing.AuthenticateAsync("nobody@b.co", "whatever")).account);
    }

    [Fact]
    public async Task A_customer_can_download_their_data_and_delete_their_account()
    {
        var (link, rowId) = await LiveDeviceAsync(paidUntilUtc: DateTime.UtcNow.AddDays(30));
        Assert.True((await SendAsync(link)).Success);
        var (_, _, db, billing) = BillingScope();
        var privacy = new PrivacyService(db, Options.Create(new PrivacyOptions { ControllerName = "Operator Ltd" }));
        int accountId = (await db.Devices.AsNoTracking().FirstAsync(d => d.Id == rowId)).AccountId!.Value;
        string email = (await db.Accounts.AsNoTracking().FirstAsync(a => a.Id == accountId)).Email;
        await billing.RecordPaymentAsync(rowId, 1, 0, 25m, "USD", "EcoCash 123", "", "admin@example.com");
        await privacy.LogAsync(email, "signed in", accountId, "", "10.0.0.1");

        var export = JObject.Parse(await privacy.ExportAsync(accountId));
        Assert.Equal(email, (string?)export["account"]!["email"]);
        Assert.Single((JArray)export["devices"]!);
        Assert.Single((JArray)export["payments"]!);
        Assert.Single((JArray)export["fiscalDocuments"]!);
        Assert.Single((JArray)export["securityLog"]!);
        Assert.DoesNotContain("PRIVATE KEY", export.ToString());
        Assert.DoesNotContain("AQAAAA", export.ToString());   // no password hash

        await privacy.DeleteAccountAsync(accountId, email, "10.0.0.1");
        db.ChangeTracker.Clear();
        Assert.False(await db.Accounts.AnyAsync(a => a.Id == accountId));
        var device = await db.Devices.FirstAsync(d => d.Id == rowId);
        Assert.Null(device.AccountId);
        // Fiscal and payment records stay; the e-mail is gone from the security log.
        Assert.Equal(1, await db.Receipts.CountAsync(r => r.DeviceRowId == rowId));
        Assert.Equal(1, await db.SubscriptionPayments.CountAsync(p => p.DeviceRowId == rowId));
        Assert.False(await db.AuditEvents.AnyAsync(e => e.Actor == email));
        Assert.Equal(2, await db.AuditEvents.CountAsync(e => e.AccountId == accountId));
        // An unlinked live device no longer sends.
        Assert.Equal("This device is not linked to an account", (await SendAsync(link)).ProblemTitle);
    }

    [Fact]
    public async Task Sign_up_records_consent_and_old_log_entries_and_files_are_deleted()
    {
        var (_, _, db, billing) = BillingScope();
        var (account, _) = await billing.CreateAccountAsync("c@example.com", "a long password", "C", "", "2026-11-16");
        Assert.Equal("2026-11-16", account!.ConsentVersion);
        Assert.NotNull(account.ConsentUtc);

        var privacy = new PrivacyService(db, Options.Create(new PrivacyOptions { AuditRetentionDays = 30, LogRetentionDays = 10 }));
        Assert.Contains(privacy.MissingSettings(), m => m.StartsWith("Privacy:PotrazLicenceNumber"));
        var now = DateTime.UtcNow;
        db.AuditEvents.Add(new AuditEvent { Utc = now.AddDays(-31), Actor = "old", Action = "signed in" });
        db.AuditEvents.Add(new AuditEvent { Utc = now.AddDays(-1), Actor = "new", Action = "signed in" });
        await db.SaveChangesAsync();

        string logs = Path.Combine(Path.GetTempPath(), "zimraegs-logs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logs);
        string oldLog = Path.Combine(logs, "log-2026-01-01.log"), newLog = Path.Combine(logs, "log-2026-11-16.log");
        File.WriteAllText(oldLog, "x"); File.SetLastWriteTimeUtc(oldLog, now.AddDays(-11));
        File.WriteAllText(newLog, "x");
        try
        {
            Assert.Equal(2, await privacy.PurgeAsync(logs, now));
            Assert.Equal(new[] { "new" }, await db.AuditEvents.Select(e => e.Actor).ToArrayAsync());
            Assert.False(File.Exists(oldLog));
            Assert.True(File.Exists(newLog));
        }
        finally { Directory.Delete(logs, true); }
    }

    [Fact]
    public async Task A_wrong_link_secret_is_not_accepted()
    {
        string link = await RegisterDeviceAsync();
        var (_, store, _) = Scope();

        Assert.NotNull(await store.FindByLinkAsync(link));
        Assert.Null(await store.FindByLinkAsync(link[..^4] + "0000"));
        Assert.Null(await store.FindByLinkAsync($"ZEGS-LINK:Production:{FakeFdms.DeviceId}:{link.Split(':')[3]}"));
    }

    [Fact]
    public void Hash_sources_follow_the_fdms_format()
    {
        var receipt = new Receipt
        {
            ReceiptType = ReceiptType.FiscalInvoice,
            ReceiptCurrency = "ZWG",
            ReceiptCounter = 2,
            ReceiptGlobalNo = 432,
            ReceiptDate = new DateTimeOffset(2019, 9, 19, 15, 43, 12, TimeSpan.FromHours(2)),
            ReceiptTotal = 9450.00m,
            ReceiptTaxes =
            [
                new ReceiptTax { TaxID = 3, TaxPercent = 15m, TaxAmount = 150.00m, SalesAmountWithTax = 1150.00m },
                new ReceiptTax { TaxID = 1, TaxPercent = null, TaxAmount = 0m, SalesAmountWithTax = 2500.00m },
                new ReceiptTax { TaxID = 2, TaxPercent = 0m, TaxAmount = 0m, SalesAmountWithTax = 3500.00m },
            ]
        };

        Assert.Equal("321FISCALINVOICEZWG4322019-09-19T15:43:12945000" + "0250000" + "0.000350000" + "15.0015000115000" + "PREV",
            ReceiptSigner.ReceiptHashSource(321, receipt, "PREV"));

        var counters = FiscalDayCounters.ToCounters(new FiscalDaySummary
        {
            TaxSummaries = [new TaxSummary { ReceiptType = ReceiptType.FiscalInvoice, ReceiptCurrency = "ZWG", TaxID = 3, TaxPersen = "15.00", TaxAmount = 2300.00m, SalesAmountWithTax = 23000.00m }],
            PaymentSummaries = [new PaymentSummary { ReceiptCurrency = "ZWG", MoneyTypeCode = MoneyType.Cash, PaymentAmount = 23000.00m }]
        });

        Assert.Equal("321842019-09-23SALEBYTAXZWG15.002300000SALETAXBYTAXZWG15.00230000BALANCEBYMONEYTYPEZWGCASH2300000",
            ReceiptSigner.FiscalDayHashSource(321, 84, new DateTimeOffset(2019, 9, 23, 8, 0, 0, TimeSpan.FromHours(2)), counters));
    }

    [Fact]
    public void Automatic_close_is_due_at_the_closing_time_or_before_the_day_limit()
    {
        var options = new AutoCloseDayOptions { TimeOfDay = "23:30", HoursBeforeLimit = 1 };
        var zone = TimeSpan.FromHours(2);
        var opened = new DateTimeOffset(2026, 3, 10, 8, 0, 0, zone);

        Assert.False(AutoCloseDayService.IsDue(opened, new DateTimeOffset(2026, 3, 10, 23, 29, 0, zone), options, 24));
        Assert.True(AutoCloseDayService.IsDue(opened, new DateTimeOffset(2026, 3, 10, 23, 31, 0, zone), options, 24));
        // Opened just after closing time: stays open through the next day, until that night.
        var late = new DateTimeOffset(2026, 3, 10, 23, 45, 0, zone);
        Assert.False(AutoCloseDayService.IsDue(late, new DateTimeOffset(2026, 3, 11, 12, 0, 0, zone), options, 24));
        Assert.True(AutoCloseDayService.IsDue(late, new DateTimeOffset(2026, 3, 11, 22, 46, 0, zone), options, 24));
    }
}
