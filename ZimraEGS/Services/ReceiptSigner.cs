using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ZimraEGS.ApiClient.Enums;
using ZimraEGS.ApiClient.Helpers;
using ZimraEGS.ApiClient.Models;
using ZimraEGS.Helpers;
using ZimraEGS.Models;

namespace ZimraEGS.Services
{
    /// <summary>
    /// Builds the strings FDMS expects devices to hash and sign, for receipts and for the fiscal day close.
    /// All amounts are decimals and are written in cents with no decimal places.
    /// </summary>
    public static class ReceiptSigner
    {
        public static string Cents(decimal amount)
        {
            return decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero).ToString("F0", CultureInfo.InvariantCulture);
        }

        public static string Percent(decimal? percent)
        {
            return percent.HasValue ? percent.Value.ToString("F2", CultureInfo.InvariantCulture) : string.Empty;
        }

        /// <summary>
        /// Tax part of the receipt hash: for every entry of receiptTaxes as sent, in taxID order,
        /// taxPercent (empty for exempt) + taxAmount + salesAmountWithTax.
        /// </summary>
        public static string ReceiptTaxesHashSource(IEnumerable<ReceiptTax> receiptTaxes)
        {
            var sb = new StringBuilder();
            foreach (var tax in receiptTaxes.OrderBy(t => t.TaxID))
            {
                sb.Append(tax.TaxCode ?? string.Empty);
                sb.Append(Percent(tax.TaxPercent));
                sb.Append(Cents(tax.TaxAmount));
                sb.Append(Cents(tax.SalesAmountWithTax));
            }
            return sb.ToString();
        }

        public static string ReceiptHashSource(int deviceID, Receipt receipt, string? previousReceiptHash)
        {
            var source = deviceID.ToString(CultureInfo.InvariantCulture) +
                receipt.ReceiptType.ToString().ToUpperInvariant() +
                receipt.ReceiptCurrency +
                receipt.ReceiptGlobalNo.ToString(CultureInfo.InvariantCulture) +
                TimeZoneHelper.ToFdmsString(receipt.ReceiptDate) +
                Cents(receipt.ReceiptTotal) +
                ReceiptTaxesHashSource(receipt.ReceiptTaxes);

            // The first receipt of a fiscal day has no previous receipt to chain to.
            if (receipt.ReceiptCounter > 1)
            {
                source += previousReceiptHash ?? string.Empty;
            }

            return source;
        }

        public static string FiscalDayCountersHashSource(IEnumerable<FiscalDayCounter> counters)
        {
            var sb = new StringBuilder();
            foreach (var item in counters)
            {
                sb.Append(item.FiscalCounterType.ToString());
                sb.Append(item.FiscalCounterCurrency);
                if (item.FiscalCounterType == FiscalCounterType.BalanceByMoneyType)
                {
                    sb.Append(item.FiscalCounterMoneyType?.ToString());
                }
                else
                {
                    sb.Append(Percent(item.FiscalCounterTaxPercent));
                }
                sb.Append(Cents(item.FiscalCounterValue));
            }
            return sb.ToString().ToUpperInvariant();
        }

        public static string FiscalDayHashSource(int deviceID, int fiscalDayNo, DateTimeOffset fiscalDayOpened, IEnumerable<FiscalDayCounter> counters)
        {
            var source = deviceID.ToString(CultureInfo.InvariantCulture) +
                fiscalDayNo.ToString(CultureInfo.InvariantCulture) +
                fiscalDayOpened.ToOffset(TimeZoneHelper.ZimbabweOffset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
                FiscalDayCountersHashSource(counters);
            return source.ToUpperInvariant();
        }

        public static SignatureData Sign(string privateKeyBase64, string source)
        {
            byte[] hash = RSA_CryptoHelper.ComputeSHA256Hash(source);
            using RSA privateKey = RSA_CryptoHelper.ConvertPrivateKeyFromBase64(privateKeyBase64);
            byte[] signature = RSA_CryptoHelper.SignDocument(privateKey, hash);
            return new SignatureData { Hash = hash, Signature = signature };
        }

        /// <summary>First 16 hex characters of the MD5 of the device signature, as used in the QR code.</summary>
        public static string SignatureMd5(byte[] signature) => Utilities.ComputeMD5(signature);

        public static string QrCode(string qrUrl, int deviceID, DateTimeOffset receiptDate, int receiptGlobalNo, byte[] signature)
        {
            return (qrUrl ?? string.Empty).TrimEnd('/') + '/' +
                deviceID.ToString(CultureInfo.InvariantCulture).PadLeft(10, '0') +
                receiptDate.ToOffset(TimeZoneHelper.ZimbabweOffset).ToString("ddMMyyyy", CultureInfo.InvariantCulture) +
                receiptGlobalNo.ToString(CultureInfo.InvariantCulture).PadLeft(10, '0') +
                SignatureMd5(signature);
        }

        public static string VerificationCode(byte[] signature)
        {
            string md5 = SignatureMd5(signature);
            return $"{md5[..4]}-{md5.Substring(4, 4)}-{md5.Substring(8, 4)}-{md5.Substring(12, 4)}";
        }
    }

    /// <summary>Turns the receipts of a fiscal day into the counters FDMS reconciles at day close.</summary>
    public static class FiscalDayCounters
    {
        public static FiscalDaySummary Summarise(Receipt receipt, int fiscalDayNo)
        {
            return new FiscalDaySummary
            {
                TaxSummaries = receipt.ReceiptTaxes.Select(t => new TaxSummary
                {
                    FiscalDayNo = fiscalDayNo,
                    ReceiptType = receipt.ReceiptType,
                    ReceiptCurrency = receipt.ReceiptCurrency,
                    TaxID = t.TaxID,
                    TaxPersen = ReceiptSigner.Percent(t.TaxPercent),
                    TaxAmount = t.TaxAmount,
                    SalesAmountWithTax = t.SalesAmountWithTax
                }).ToList(),
                PaymentSummaries = receipt.ReceiptPayments.Select(p => new PaymentSummary
                {
                    FiscalDayNo = fiscalDayNo,
                    ReceiptCurrency = receipt.ReceiptCurrency,
                    MoneyTypeCode = p.MoneyTypeCode,
                    PaymentAmount = p.PaymentAmount
                }).ToList()
            };
        }

        public static FiscalDaySummary Combine(IEnumerable<FiscalDaySummary?> summaries)
        {
            var all = summaries.Where(s => s != null).Select(s => s!).ToList();

            return new FiscalDaySummary
            {
                TaxSummaries = all.SelectMany(s => s.TaxSummaries ?? [])
                    .GroupBy(r => new { r.ReceiptType, r.ReceiptCurrency, r.TaxID, TaxPersen = r.TaxPersen ?? string.Empty })
                    .Select(g => new TaxSummary
                    {
                        FiscalDayNo = g.First().FiscalDayNo,
                        ReceiptType = g.Key.ReceiptType,
                        ReceiptCurrency = g.Key.ReceiptCurrency,
                        TaxID = g.Key.TaxID,
                        TaxPersen = g.Key.TaxPersen,
                        TaxAmount = g.Sum(r => r.TaxAmount),
                        SalesAmountWithTax = g.Sum(r => r.SalesAmountWithTax)
                    }).ToList(),
                PaymentSummaries = all.SelectMany(s => s.PaymentSummaries ?? [])
                    .GroupBy(p => new { p.ReceiptCurrency, p.MoneyTypeCode })
                    .Select(g => new PaymentSummary
                    {
                        FiscalDayNo = g.First().FiscalDayNo,
                        ReceiptCurrency = g.Key.ReceiptCurrency,
                        MoneyTypeCode = g.Key.MoneyTypeCode,
                        PaymentAmount = g.Sum(p => p.PaymentAmount)
                    }).ToList()
            };
        }

        public static List<FiscalDayCounter> ToCounters(FiscalDaySummary summary)
        {
            var counters = new List<FiscalDayCounter>();

            foreach (var item in summary.TaxSummaries ?? [])
            {
                var (salesType, taxType) = item.ReceiptType switch
                {
                    ReceiptType.FiscalInvoice => (FiscalCounterType.SaleByTax, FiscalCounterType.SaleTaxByTax),
                    ReceiptType.CreditNote => (FiscalCounterType.CreditNoteByTax, FiscalCounterType.CreditNoteTaxByTax),
                    ReceiptType.DebitNote => (FiscalCounterType.DebitNoteByTax, FiscalCounterType.DebitNoteTaxByTax),
                    _ => throw new InvalidOperationException($"Unsupported receipt type: {item.ReceiptType}")
                };

                decimal? percent = string.IsNullOrWhiteSpace(item.TaxPersen)
                    ? null
                    : decimal.Parse(item.TaxPersen, NumberStyles.Number, CultureInfo.InvariantCulture);

                counters.Add(new FiscalDayCounter
                {
                    FiscalCounterType = salesType,
                    FiscalCounterCurrency = item.ReceiptCurrency,
                    FiscalCounterTaxID = item.TaxID,
                    FiscalCounterTaxPercent = percent,
                    FiscalCounterValue = item.SalesAmountWithTax
                });
                counters.Add(new FiscalDayCounter
                {
                    FiscalCounterType = taxType,
                    FiscalCounterCurrency = item.ReceiptCurrency,
                    FiscalCounterTaxID = item.TaxID,
                    FiscalCounterTaxPercent = percent,
                    FiscalCounterValue = item.TaxAmount
                });
            }

            foreach (var item in summary.PaymentSummaries ?? [])
            {
                counters.Add(new FiscalDayCounter
                {
                    FiscalCounterType = FiscalCounterType.BalanceByMoneyType,
                    FiscalCounterCurrency = item.ReceiptCurrency,
                    FiscalCounterMoneyType = item.MoneyTypeCode,
                    FiscalCounterValue = item.PaymentAmount
                });
            }

            // Zero value counters are not submitted.
            return counters.Where(c => c.FiscalCounterValue != 0)
                .OrderBy(c => c.FiscalCounterType)
                .ThenBy(c => c.FiscalCounterCurrency, StringComparer.Ordinal)
                .ThenBy(c => c.FiscalCounterTaxID)
                .ThenBy(c => c.FiscalCounterMoneyType)
                .ToList();
        }
    }
}
