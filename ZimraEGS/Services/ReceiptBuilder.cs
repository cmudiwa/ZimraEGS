using ZimraEGS.ApiClient.Enums;
using ZimraEGS.ApiClient.Models;
using ZimraEGS.Helpers;
using ZimraEGS.Models;

namespace ZimraEGS.Services
{
    public class ReceiptDraft
    {
        /// <summary>Receipt content without counters, date and signature, which are assigned at submission.</summary>
        public Receipt Receipt { get; set; } = new Receipt();

        /// <summary>Problems the user has to fix in Manager before the receipt can be sent.</summary>
        public List<string> Problems { get; set; } = [];
    }

    /// <summary>Converts a relayed Manager invoice or credit note into a ZIMRA receipt.</summary>
    public static class ReceiptBuilder
    {
        private static readonly string[] AcceptedCurrencies = ["ZWG", "USD"];

        private static decimal Round(decimal value, int decimals) => decimal.Round(value, decimals, MidpointRounding.AwayFromZero);

        public static ReceiptDraft Build(RelayData relay, GetConfigResponse? config, bool sellerIsVatRegistered, MoneyType? defaultMoneyType = null)
        {
            var draft = new ReceiptDraft();
            var problems = draft.Problems;
            var receipt = draft.Receipt;
            var invoice = relay.ManagerInvoice;

            receipt.ReceiptType = relay.ReceiptType;
            receipt.ReceiptCurrency = string.IsNullOrEmpty(relay.CurrencyCode) ? "ZWG" : relay.CurrencyCode;
            receipt.ReceiptPrintForm = relay.ReceiptPrintForm == "Receipt48" ? ReceiptPrintForm.Receipt48 : ReceiptPrintForm.InvoiceA4;
            receipt.ReceiptLinesTaxInclusive = invoice?.AmountsIncludeTax ?? false;

            if (!AcceptedCurrencies.Contains(receipt.ReceiptCurrency))
            {
                problems.Add($"Currency {receipt.ReceiptCurrency} is not accepted. Use ZWG or USD.");
            }

            receipt.BuyerData = BuildBuyer(relay, problems);

            if (receipt.ReceiptType != ReceiptType.FiscalInvoice)
            {
                receipt.ReceiptNotes = relay.ReceiptNotes;
                receipt.CreditDebitNote = new CreditDebitNote
                {
                    ReceiptID = relay.ReceiptID,
                    DeviceID = relay.DeviceIDRef,
                    ReceiptGlobalNo = relay.ReceiptGlobalNoRef,
                    FiscalDayNo = relay.FiscalDayNoRef,
                    ReceiptRefDate = relay.ReceiptRefDate
                };

                string noteName = receipt.ReceiptType == ReceiptType.CreditNote ? "credit note" : "debit note";

                if (relay.ReceiptID == 0 || relay.ReceiptGlobalNoRef == 0)
                {
                    problems.Add($"This {noteName} must refer to an invoice that has already been fiscalised. Select the invoice on the {noteName} and make sure that invoice was sent to ZIMRA first.");
                }
                else if (TimeZoneHelper.GetSouthAfricaTime() - relay.ReceiptRefDate > TimeSpan.FromDays(365))
                {
                    problems.Add("The original invoice was issued more than 12 months ago, so ZIMRA will not accept a note against it.");
                }

                if (string.IsNullOrWhiteSpace(relay.ReceiptNotes))
                {
                    problems.Add($"Enter the reason for this {noteName} in the Receipt Notes field.");
                }
            }

            var applicableTaxes = ValidTaxes(config);
            var lines = BuildLines(relay, applicableTaxes, sellerIsVatRegistered, problems);
            receipt.ReceiptLines = lines;

            if (lines.Count == 0)
            {
                problems.Add("The invoice has no lines.");
            }

            receipt.ReceiptTaxes = BuildTaxes(lines, receipt.ReceiptLinesTaxInclusive);
            receipt.ReceiptTotal = receipt.ReceiptTaxes.Sum(x => x.SalesAmountWithTax);
            int problemsBeforePayments = problems.Count;
            receipt.ReceiptPayments = BuildPayments(relay, receipt.ReceiptTotal, defaultMoneyType, problems);

            if (receipt.ReceiptPayments.Count == 0)
            {
                if (problems.Count == problemsBeforePayments)
                {
                    problems.Add("ZIMRA needs to know how this invoice is paid. Choose a Payment Type on the invoice, or set a default payment type below so you never have to.");
                }
            }
            else
            {
                decimal paid = receipt.ReceiptPayments.Sum(x => x.PaymentAmount);
                if (paid != receipt.ReceiptTotal)
                {
                    problems.Add($"The payment amounts add up to {Math.Abs(paid):0.00} but the invoice total is {Math.Abs(receipt.ReceiptTotal):0.00}. They must be equal.");
                }
            }

            return draft;
        }

        private static Buyer? BuildBuyer(RelayData relay, List<string> problems)
        {
            bool hasName = !string.IsNullOrWhiteSpace(relay.BuyerRegisterName);
            bool hasTin = !string.IsNullOrWhiteSpace(relay.BuyerTIN);

            // Buyer details are optional as a whole (for example a cash sale to a walk-in customer).
            if (!hasName && !hasTin)
            {
                return null;
            }

            if (!hasName || !hasTin)
            {
                problems.Add("The customer needs both a Registered Name and a TIN. Complete the customer's details or remove both.");
            }

            // ZIMRA rejects a contact block without an email or phone number, and an address block is only
            // meaningful with content, so blocks with nothing in them are left out altogether.
            bool hasContacts = !string.IsNullOrWhiteSpace(relay.BuyerEmail) || !string.IsNullOrWhiteSpace(relay.BuyerPhoneNo);
            bool hasAddress = new[] { relay.BuyerProvince, relay.BuyerCity, relay.BuyerStreet, relay.BuyerHouseNo }.Any(x => !string.IsNullOrWhiteSpace(x));

            return new Buyer
            {
                BuyerRegisterName = relay.BuyerRegisterName,
                BuyerTradeName = NullIfEmpty(relay.BuyerTradeName)!,
                BuyerTIN = relay.BuyerTIN,
                VatNumber = NullIfEmpty(relay.BuyerVatNumber)!,
                BuyerContacts = hasContacts ? new Contacts
                {
                    Email = NullIfEmpty(relay.BuyerEmail)!,
                    PhoneNo = NullIfEmpty(relay.BuyerPhoneNo)!,
                } : null!,
                BuyerAddress = hasAddress ? new AddressDto
                {
                    Province = relay.BuyerProvince ?? string.Empty,
                    City = relay.BuyerCity ?? string.Empty,
                    Street = relay.BuyerStreet ?? string.Empty,
                    HouseNo = relay.BuyerHouseNo ?? string.Empty,
                } : null!,
            };
        }

        private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

        private static List<Tax> ValidTaxes(GetConfigResponse? config)
        {
            if (config?.ApplicableTaxes == null) return [];
            var now = TimeZoneHelper.GetSouthAfricaTime();
            return config.ApplicableTaxes
                .Where(t => t.ValidFrom <= now && (t.ValidTill == null || t.ValidTill.Value.AddDays(1) > now))
                .ToList();
        }

        /// <summary>
        /// Works out the ZIMRA tax for a Manager line. Tax codes named "Name — taxID — ..." carry the ID
        /// directly; otherwise the tax is matched on its rate against the taxes ZIMRA configured for the device.
        /// </summary>
        private static (int taxID, decimal? taxPercent) ResolveTax(Line line, List<Tax> applicableTaxes, string lineLabel, List<string> problems)
        {
            decimal rate = line.TaxCode?.Rate ?? 0;
            int? taxID = null;

            if (line.TaxCode?.Name != null && line.TaxCode.Name.Contains('—'))
            {
                var parts = line.TaxCode.Name.Split('—');
                if (parts.Length > 1 && int.TryParse(parts[1].Trim(), out int parsed))
                {
                    taxID = parsed;
                }
            }

            if (applicableTaxes.Count == 0)
            {
                // No device configuration available: fall back to the fixed mapping of the test environment.
                int id = taxID ?? 1;
                return (id, id == 1 ? null : rate);
            }

            if (taxID == null)
            {
                List<Tax> candidates = line.TaxCode == null
                    ? applicableTaxes.Where(t => t.TaxPercent == null).ToList()
                    : applicableTaxes.Where(t => t.TaxPercent == rate).ToList();

                if (candidates.Count == 1)
                {
                    taxID = candidates[0].TaxID;
                }
                else
                {
                    string taxName = line.TaxCode?.Name ?? "no tax code";
                    problems.Add($"{lineLabel}: cannot tell which ZIMRA tax applies to \"{taxName}\". Name the tax code like \"Standard rated 15% — 3 —\" using a tax ID from: {DescribeTaxes(applicableTaxes)}.");
                    return (0, rate);
                }
            }

            var tax = applicableTaxes.FirstOrDefault(t => t.TaxID == taxID);
            if (tax == null)
            {
                problems.Add($"{lineLabel}: tax ID {taxID} is not one of the taxes ZIMRA allows for this device ({DescribeTaxes(applicableTaxes)}).");
                return (taxID.Value, rate);
            }

            if (tax.TaxPercent.HasValue && tax.TaxPercent.Value != rate)
            {
                problems.Add($"{lineLabel}: the tax code rate is {rate:0.##}% but ZIMRA tax {tax.TaxID} ({tax.TaxName}) is {tax.TaxPercent:0.##}%.");
            }
            else if (!tax.TaxPercent.HasValue && rate != 0)
            {
                problems.Add($"{lineLabel}: ZIMRA tax {tax.TaxID} ({tax.TaxName}) is exempt but the tax code charges {rate:0.##}%.");
            }

            return (tax.TaxID, tax.TaxPercent);
        }

        private static string DescribeTaxes(List<Tax> taxes)
        {
            return string.Join(", ", taxes.OrderBy(t => t.TaxID).Select(t => $"{t.TaxID} = {t.TaxName}"));
        }

        private static List<ReceiptLine> BuildLines(RelayData relay, List<Tax> applicableTaxes, bool sellerIsVatRegistered, List<string> problems)
        {
            var result = new List<ReceiptLine>();
            var invoice = relay.ManagerInvoice;
            if (invoice?.Lines == null) return result;

            // Manager keeps credit notes (and debit notes, entered as credit notes with negative prices)
            // with the opposite sign of what ZIMRA expects: credit note amounts are negative, debit note positive.
            decimal sign = relay.ReceiptType == ReceiptType.FiscalInvoice ? 1 : -1;

            var resolved = new List<(Line line, string name, string hsCode, int taxID, decimal? taxPercent)>();
            int number = 1;

            foreach (Line line in invoice.Lines)
            {
                string label = $"Line {number}";
                string name = FirstNonEmpty(line.LineDescription, line.Item?.Name, line.Item?.ItemName);
                string hsCode = string.Empty;
                // The HS code can be set on the line or, as a default, on the inventory item.
                if (line.CustomFields2?.Strings != null && line.CustomFields2.Strings.TryGetValue(ManagerCustomField.HsCodeGuid, out string? value) && !string.IsNullOrWhiteSpace(value))
                {
                    hsCode = value.Trim();
                }
                else if (line.Item?.CustomFields2?.Strings != null && line.Item.CustomFields2.Strings.TryGetValue(ManagerCustomField.HsCodeGuid, out string? itemValue))
                {
                    hsCode = (itemValue ?? string.Empty).Trim();
                }

                if (string.IsNullOrWhiteSpace(name))
                {
                    problems.Add($"{label}: has no item name or description.");
                }
                else if (name.Length > 200)
                {
                    name = name[..200];
                }

                var (taxID, taxPercent) = ResolveTax(line, applicableTaxes, $"{label} ({name})", problems);

                // ZIMRA: a VAT payer must send an HS code. It has 4 or 8 digits, and exactly 8 for a VAT payer's
                // zero-rated or exempt line. The code is sent as entered; padding it would change its meaning.
                bool zeroOrExempt = taxPercent is null or 0;
                if (string.IsNullOrEmpty(hsCode))
                {
                    if (sellerIsVatRegistered)
                    {
                        problems.Add($"{label} ({name}): the HS Code is missing. Set it on the inventory item or the line.");
                    }
                }
                else if (!hsCode.All(char.IsDigit) || (hsCode.Length != 4 && hsCode.Length != 8))
                {
                    problems.Add($"{label} ({name}): HS Code \"{hsCode}\" must have 4 or 8 digits.");
                }
                else if (sellerIsVatRegistered && zeroOrExempt && hsCode.Length != 8)
                {
                    problems.Add($"{label} ({name}): a zero-rated or exempt line needs the full 8-digit HS Code, not \"{hsCode}\".");
                }

                if (line.Qty <= 0)
                {
                    problems.Add($"{label} ({name}): the quantity must be greater than zero.");
                }

                string? sentHsCode = string.IsNullOrEmpty(hsCode) ? null : hsCode;
                resolved.Add((line, name, sentHsCode ?? string.Empty, taxID, taxPercent));

                decimal price = sign * Round(line.UnitPrice, 4);

                result.Add(new ReceiptLine
                {
                    ReceiptLineType = ReceiptLineType.Sale,
                    ReceiptLineNo = number,
                    ReceiptLineName = name,
                    ReceiptLineHSCode = sentHsCode!,
                    ReceiptLineQuantity = Round(line.Qty, 2),
                    ReceiptLinePrice = price,
                    ReceiptLineTotal = Round(line.Qty * price, 2),
                    TaxID = taxID,
                    TaxPercent = taxPercent
                });
                number++;
            }

            // Discounts follow as separate lines, each with the tax of the line it reduces.
            foreach (var (line, name, hsCode, taxID, taxPercent) in resolved)
            {
                if (line.DiscountAmount <= 0) continue;

                decimal qty = line.Qty == 0 ? 1 : line.Qty;
                decimal price = -sign * Round(line.DiscountAmount / qty, 4);

                result.Add(new ReceiptLine
                {
                    ReceiptLineType = ReceiptLineType.Discount,
                    ReceiptLineNo = number,
                    ReceiptLineName = ("Discount: " + name).Length > 200 ? ("Discount: " + name)[..200] : "Discount: " + name,
                    ReceiptLineHSCode = string.IsNullOrEmpty(hsCode) ? null! : hsCode,
                    ReceiptLineQuantity = Round(qty, 2),
                    ReceiptLinePrice = price,
                    ReceiptLineTotal = Round(qty * price, 2),
                    TaxID = taxID,
                    TaxPercent = taxPercent
                });
                number++;
            }

            return result;
        }

        private static string FirstNonEmpty(params string?[] values)
        {
            return values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
        }

        /// <summary>One receiptTaxes entry per tax, totalled over the lines carrying that tax.</summary>
        private static List<ReceiptTax> BuildTaxes(List<ReceiptLine> lines, bool taxInclusive)
        {
            return lines
                .GroupBy(l => new { l.TaxID, l.TaxPercent })
                .Select(g =>
                {
                    decimal rate = (g.Key.TaxPercent ?? 0) / 100m;
                    decimal taxAmount = 0;
                    decimal salesWithTax = 0;

                    foreach (var line in g)
                    {
                        if (taxInclusive)
                        {
                            decimal lineTax = Round(line.ReceiptLineTotal - line.ReceiptLineTotal / (1 + rate), 2);
                            taxAmount += lineTax;
                            salesWithTax += line.ReceiptLineTotal;
                        }
                        else
                        {
                            decimal lineTax = Round(line.ReceiptLineTotal * rate, 2);
                            taxAmount += lineTax;
                            salesWithTax += line.ReceiptLineTotal + lineTax;
                        }
                    }

                    return new ReceiptTax
                    {
                        TaxID = g.Key.TaxID,
                        TaxPercent = g.Key.TaxPercent,
                        TaxAmount = taxAmount,
                        SalesAmountWithTax = salesWithTax
                    };
                })
                .OrderBy(t => t.TaxID)
                .ToList();
        }

        private static MoneyType? ParseMoneyType(string? type)
        {
            // Payment types are dropdown values such as "0 | Cash": the leading digit is the ZIMRA money type.
            string trimmed = (type ?? string.Empty).Trim();
            if (trimmed.Length > 0 && int.TryParse(trimmed[..1], out int code) && Enum.IsDefined(typeof(MoneyType), code))
            {
                return (MoneyType)code;
            }
            return null;
        }

        /// <summary>
        /// Payments of the receipt. Amounts are optional: with no amounts entered the whole total is taken as
        /// paid by the payment type chosen on the invoice or, failing that, the device's default payment type.
        /// </summary>
        private static List<Payment> BuildPayments(RelayData relay, decimal receiptTotal, MoneyType? defaultMoneyType, List<string> problems)
        {
            var payments = new List<Payment>();

            if (relay.PaymentAmount1 <= 0 && relay.PaymentAmount2 <= 0)
            {
                MoneyType? type = ParseMoneyType(relay.PaymentType1) ?? ParseMoneyType(relay.PaymentType2) ?? defaultMoneyType;
                if (type != null)
                {
                    payments.Add(new Payment { MoneyTypeCode = type.Value, PaymentAmount = receiptTotal });
                }
                return payments;
            }

            decimal sign = relay.ReceiptType == ReceiptType.CreditNote ? -1 : 1;

            AddPayment(payments, relay.PaymentType1, relay.PaymentAmount1, sign, "Payment Type 1", problems);
            AddPayment(payments, relay.PaymentType2, relay.PaymentAmount2, sign, "Payment Type 2", problems);

            return payments;
        }

        private static void AddPayment(List<Payment> payments, string? type, decimal amount, decimal sign, string fieldName, List<string> problems)
        {
            if (amount <= 0) return;

            if (ParseMoneyType(type) is { } moneyType)
            {
                payments.Add(new Payment { MoneyTypeCode = moneyType, PaymentAmount = sign * Round(amount, 2) });
            }
            else
            {
                problems.Add($"{fieldName} is not set although an amount of {amount:0.00} was entered.");
            }
        }
    }
}
