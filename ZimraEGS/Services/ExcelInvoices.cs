using System.Globalization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using ZimraEGS.ApiClient.Enums;
using ZimraEGS.ApiClient.Models;
using ZimraEGS.Data;
using ZimraEGS.Helpers;

namespace ZimraEGS.Services
{
    /// <summary>One invoice, credit note or debit note read from a workbook: one or more rows with the same invoice number.</summary>
    public class ExcelDocument
    {
        public ReceiptType Type { get; set; } = ReceiptType.FiscalInvoice;
        /// <summary>The customer's own invoice number. A document is never fiscalised twice under the same number.</summary>
        public string Reference { get; set; } = string.Empty;
        public string Currency { get; set; } = string.Empty;
        public bool TaxInclusive { get; set; }
        public string BuyerName { get; set; } = string.Empty;
        public string BuyerTin { get; set; } = string.Empty;
        public string BuyerVat { get; set; } = string.Empty;
        public string BuyerEmail { get; set; } = string.Empty;
        public string BuyerPhone { get; set; } = string.Empty;
        public string BuyerStreet { get; set; } = string.Empty;
        public string BuyerCity { get; set; } = string.Empty;
        public string BuyerProvince { get; set; } = string.Empty;
        public MoneyType? MoneyType { get; set; }
        /// <summary>Credit and debit notes: the invoice they correct, as its fiscal number or the customer's own number.</summary>
        public string OriginalReference { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public List<ExcelLine> Lines { get; set; } = [];
        public List<int> Rows { get; set; } = [];
        public List<string> Problems { get; set; } = [];

        // As typed, before checking.
        internal string? TypeText { get; set; }
        internal string? InclusiveText { get; set; }
        internal string? PaidText { get; set; }
        internal Dictionary<string, string> Seen { get; } = [];

        public string TypeName => Type switch { ReceiptType.CreditNote => "Credit note", ReceiptType.DebitNote => "Debit note", _ => "Invoice" };

        public static string FormKeyFor(string reference) => "excel:" + reference.Trim().ToLowerInvariant();
    }

    public class ExcelLine
    {
        public int Row { get; set; }
        public string Description { get; set; } = string.Empty;
        public string HsCode { get; set; } = string.Empty;
        public decimal Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal Discount { get; set; }
        public int? TaxID { get; set; }
        public string TaxName { get; set; } = string.Empty;
        public decimal? TaxPercent { get; set; }
    }

    /// <summary>The outcome for one document, written back into the workbook.</summary>
    public class ExcelResult
    {
        /// <summary>The customer's own invoice number, as typed.</summary>
        public string Reference { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string FiscalNumber { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string VerificationCode { get; set; } = string.Empty;
        public string QrLink { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public int? ReceiptRowId { get; set; }
    }

    /// <summary>The invoice workbook: the template customers fill in, reading it, and writing the results back.</summary>
    public static class ExcelInvoices
    {
        public const string SheetName = "Invoices";
        public const int MaxRows = 3000;
        public const int MaxDocuments = 300;

        private record Column(string Key, string Header, double Width, string Help);

        private static readonly Column[] Columns =
        [
            new("type", "Type", 13, "Invoice, Credit note or Debit note."),
            new("ref", "Your invoice no.", 16, "Your own number. Every row with the same number is one document. Never reuse a number: a number already sent is not sent again."),
            new("currency", "Currency", 10, "USD or ZWG."),
            new("inclusive", "Prices include tax", 12, "Yes if the unit prices already include VAT, No if VAT is added on top. Leave empty for No."),
            new("buyer", "Customer name", 24, "Registered name. Optional for a walk-in customer; when given, the TIN is needed too."),
            new("tin", "Customer TIN", 14, "10 digits."),
            new("vat", "Customer VAT no.", 14, "If the customer is VAT registered."),
            new("email", "Customer e-mail", 20, "Optional."),
            new("phone", "Customer phone", 14, "Optional."),
            new("street", "Customer address", 22, "Optional: street and number."),
            new("city", "Customer city", 12, "Optional."),
            new("province", "Customer province", 12, "Optional."),
            new("desc", "Description", 28, "What was sold."),
            new("hs", "HS code", 11, "4 or 8 digits; 8 digits for zero-rated or exempt lines. Services: 99001000, 99002000 or 99003000."),
            new("qty", "Quantity", 9, "More than 0."),
            new("price", "Unit price", 11, "Positive, also on credit and debit notes."),
            new("discount", "Discount", 10, "Optional: discount amount for the line."),
            new("tax", "Tax", 26, "Pick from the list: these are the taxes ZIMRA set for your device."),
            new("paid", "Paid by", 16, "Cash, Card, Mobile wallet, Coupon, Credit, Bank transfer or Other. Empty: the device's default."),
            new("original", "Original invoice no.", 18, "Credit and debit notes only: the invoice they correct (its fiscal number, or your own number if it was sent from Excel)."),
            new("reason", "Reason", 22, "Credit and debit notes only: why, e.g. Goods returned."),
            new("r_fiscal", "Fiscal invoice no.", 17, "Filled in by the server."),
            new("r_status", "Status", 12, "Filled in by the server."),
            new("r_code", "Verification code", 21, "Filled in by the server."),
            new("r_qr", "QR link", 30, "Filled in by the server."),
            new("r_message", "Message", 40, "Filled in by the server."),
        ];

        private static readonly (MoneyType type, string name)[] Payments =
        [
            (MoneyType.Cash, "Cash"), (MoneyType.Card, "Card"), (MoneyType.MobileWallet, "Mobile wallet"), (MoneyType.Coupon, "Coupon"),
            (MoneyType.Credit, "Credit"), (MoneyType.BankTransfer, "Bank transfer"), (MoneyType.Other, "Other"),
        ];

        private static string Normalise(string s) => Regex.Replace((s ?? string.Empty).ToLowerInvariant(), "[^a-z0-9]", "");

        public static List<Tax> ValidTaxes(Device device)
        {
            var now = TimeZoneHelper.GetSouthAfricaTime();
            return (DeviceStore.GetConfig(device)?.ApplicableTaxes ?? [])
                .Where(t => t.ValidFrom <= now && (t.ValidTill == null || t.ValidTill.Value.AddDays(1) > now))
                .OrderBy(t => t.TaxID).ToList();
        }

        private static string TaxLabel(Tax t) => $"{t.TaxID} - {t.TaxName}";

        // ------------------------------------------------------------------ template

        public static byte[] BuildTemplate(Device device)
        {
            var taxes = ValidTaxes(device);
            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add(SheetName);
            var lists = wb.Worksheets.Add("Lists");
            var help = wb.Worksheets.Add("How to");

            for (int c = 0; c < Columns.Length; c++)
            {
                var col = Columns[c];
                var cell = ws.Cell(1, c + 1);
                cell.Value = col.Header;
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = col.Key.StartsWith("r_") ? XLColor.FromHtml("#E7E6E6") : XLColor.FromHtml("#F4CCCC");
                cell.Style.Alignment.WrapText = true;
                cell.CreateComment().AddText(col.Help);
                ws.Column(c + 1).Width = col.Width;
            }
            ws.Row(1).Height = 32;
            ws.SheetView.FreezeRows(1);

            int Col(string key) => Array.FindIndex(Columns, x => x.Key == key) + 1;
            // Text columns: Excel must not drop the leading zeros of HS codes, TINs or phone numbers.
            foreach (var key in new[] { "ref", "tin", "vat", "phone", "hs", "original" })
            {
                ws.Range(2, Col(key), MaxRows + 1, Col(key)).Style.NumberFormat.Format = "@";
            }

            // Drop-down lists.
            string[] types = ["Invoice", "Credit note", "Debit note"];
            string[] currencies = ["USD", "ZWG"];
            string[] yesNo = ["No", "Yes"];
            void List(int listColumn, string[] values, string key)
            {
                for (int i = 0; i < values.Length; i++) lists.Cell(i + 1, listColumn).Value = values[i];
                if (values.Length == 0) return;
                var range = lists.Range(1, listColumn, values.Length, listColumn);
                var validation = ws.Range(2, Col(key), MaxRows + 1, Col(key)).CreateDataValidation();
                validation.List(range, true);
                validation.IgnoreBlanks = true;
            }
            List(1, types, "type");
            List(2, currencies, "currency");
            List(3, yesNo, "inclusive");
            List(4, taxes.Select(TaxLabel).ToArray(), "tax");
            List(5, Payments.Select(p => p.name).ToArray(), "paid");
            lists.Visibility = XLWorksheetVisibility.Hidden;

            // Two example rows: one invoice with two lines.
            var standard = taxes.FirstOrDefault(t => t.TaxPercent > 0) ?? taxes.FirstOrDefault();
            var zero = taxes.FirstOrDefault(t => t.TaxPercent == 0) ?? standard;
            object?[][] examples =
            [
                ["Invoice", "INV-0001", "USD", "No", "Example Customer (Pvt) Ltd", "2000000001", "", "", "", "", "Harare", "", "Consulting services", "99001000", 2, 50, 0, standard == null ? "" : TaxLabel(standard), "Bank transfer"],
                ["Invoice", "INV-0001", "USD", "No", "Example Customer (Pvt) Ltd", "2000000001", "", "", "", "", "Harare", "", "Bread", "19059010", 10, 1.25, 0, zero == null ? "" : TaxLabel(zero), "Bank transfer"],
            ];
            for (int r = 0; r < examples.Length; r++)
            {
                for (int c = 0; c < examples[r].Length; c++)
                {
                    var v = examples[r][c];
                    ws.Cell(r + 2, c + 1).Value = v switch { int i => i, double d => d, string s => s, _ => "" };
                }
            }

            var info = DeviceStore.GetInfo(device);
            string[] instructions =
            [
                "Fiscal invoices from Excel",
                "",
                $"This workbook is for device {device.DeviceID} ({(device.Environment == PlatformType.Production ? "live" : "test")}), {info.TaxPayerName}.",
                "",
                "1. Fill in the Invoices sheet: one row per line. Rows with the same 'Your invoice no.' make one document.",
                "   Fill the customer, currency, payment and tax-inclusive columns on the first row of a document; the other rows may leave them empty.",
                "2. Replace or delete the two example rows (INV-0001).",
                "3. Save the workbook and upload it on the Excel page. Every document is checked before anything is sent.",
                "4. Send. Each document is fiscalised once: uploading the same number again does not send it again.",
                "5. Download the workbook with results: the fiscal number, status, verification code and QR link are filled in.",
                "   Print each document from the results page: that printout, in ZIMRA's layout, is the fiscal document.",
                "",
                "Credit and debit notes: put the invoice they correct in 'Original invoice no.' and the reason in 'Reason'.",
                "Prices stay positive on credit and debit notes.",
                "Point at a column heading in the Invoices sheet to see what goes in it.",
            ];
            for (int i = 0; i < instructions.Length; i++) help.Cell(i + 1, 1).Value = instructions[i];
            help.Cell(1, 1).Style.Font.Bold = true;
            help.Cell(1, 1).Style.Font.FontSize = 14;
            help.Column(1).Width = 120;

            ws.SetTabActive();
            using var stream = new MemoryStream();
            wb.SaveAs(stream);
            return stream.ToArray();
        }

        // ------------------------------------------------------------------ reading

        /// <summary>Reads the documents in a workbook. File-level problems (no sheet, missing columns) come back on their own.</summary>
        public static (List<ExcelDocument> documents, List<string> problems) Read(Stream stream, Device device)
        {
            var documents = new List<ExcelDocument>();
            var problems = new List<string>();
            XLWorkbook wb;
            try { wb = new XLWorkbook(stream); }
            catch (Exception)
            {
                problems.Add("The file could not be read as an Excel workbook (.xlsx). Save it from Excel as 'Excel Workbook' and try again.");
                return (documents, problems);
            }

            using (wb)
            {
                var ws = wb.Worksheets.FirstOrDefault(w => string.Equals(w.Name, SheetName, StringComparison.OrdinalIgnoreCase)) ?? wb.Worksheets.First();
                var map = HeaderMap(ws);
                var required = new[] { "type", "ref", "desc", "qty", "price", "tax" };
                var missing = required.Where(k => !map.ContainsKey(k)).Select(k => Columns.First(c => c.Key == k).Header).ToList();
                if (missing.Count > 0)
                {
                    problems.Add($"The sheet '{ws.Name}' has no column {string.Join(", ", missing.Select(m => "'" + m + "'"))}. Download the template and copy your rows into it.");
                    return (documents, problems);
                }

                int last = ws.LastRowUsed()?.RowNumber() ?? 1;
                if (last - 1 > MaxRows)
                {
                    problems.Add($"The sheet has {last - 1} rows. Upload at most {MaxRows} rows at a time.");
                    return (documents, problems);
                }

                var taxes = ValidTaxes(device);
                var byReference = new Dictionary<string, ExcelDocument>();
                for (int row = 2; row <= last; row++)
                {
                    string Text(string key) => map.TryGetValue(key, out int c) ? CellText(ws.Cell(row, c), key == "hs") : string.Empty;
                    var lineKeys = new[] { "type", "ref", "desc", "hs", "qty", "price", "tax" };
                    if (lineKeys.All(k => string.IsNullOrWhiteSpace(Text(k)))) continue;   // empty row

                    string reference = Text("ref");
                    if (string.IsNullOrWhiteSpace(reference))
                    {
                        problems.Add($"Row {row}: 'Your invoice no.' is empty. Every row needs the number of the document it belongs to.");
                        continue;
                    }

                    string key = reference.Trim().ToLowerInvariant();
                    if (!byReference.TryGetValue(key, out var doc))
                    {
                        if (byReference.Count >= MaxDocuments)
                        {
                            problems.Add($"The workbook has more than {MaxDocuments} documents. Upload at most {MaxDocuments} at a time.");
                            break;
                        }
                        doc = new ExcelDocument { Reference = reference.Trim() };
                        byReference[key] = doc;
                        documents.Add(doc);
                    }
                    doc.Rows.Add(row);

                    // Document-level values: taken from the first row that has them; a different value later is an error.
                    void Same(string column, string value, Action<string> set, ref string? seen)
                    {
                        if (string.IsNullOrWhiteSpace(value)) return;
                        if (seen == null) { seen = value.Trim(); set(seen); }
                        else if (!string.Equals(seen, value.Trim(), StringComparison.OrdinalIgnoreCase))
                            doc.Problems.Add($"Row {row}: '{Columns.First(c => c.Key == column).Header}' is \"{value.Trim()}\" but an earlier row of this document says \"{seen}\".");
                    }
                    var seen = doc.Seen;
                    foreach (var (column, set) in new (string, Action<string>)[]
                    {
                        ("type", v => doc.TypeText = v), ("currency", v => doc.Currency = v.ToUpperInvariant()), ("inclusive", v => doc.InclusiveText = v),
                        ("buyer", v => doc.BuyerName = v), ("tin", v => doc.BuyerTin = v), ("vat", v => doc.BuyerVat = v),
                        ("email", v => doc.BuyerEmail = v), ("phone", v => doc.BuyerPhone = v), ("street", v => doc.BuyerStreet = v),
                        ("city", v => doc.BuyerCity = v), ("province", v => doc.BuyerProvince = v), ("paid", v => doc.PaidText = v),
                        ("original", v => doc.OriginalReference = v), ("reason", v => doc.Reason = v),
                    })
                    {
                        seen.TryGetValue(column, out string? s);
                        Same(column, Text(column), set, ref s);
                        if (s != null) seen[column] = s;
                    }

                    doc.Lines.Add(ReadLine(row, Text, taxes, doc.Problems, map.TryGetValue("hs", out int hsColumn) ? ws.Cell(row, hsColumn) : null));
                }

                foreach (var doc in documents) Finish(doc, device);
            }
            return (documents, problems);
        }

        private static Dictionary<string, int> HeaderMap(IXLWorksheet ws)
        {
            var map = new Dictionary<string, int>();
            var byHeader = Columns.ToDictionary(c => Normalise(c.Header), c => c.Key);
            foreach (var cell in ws.Row(1).CellsUsed())
            {
                if (byHeader.TryGetValue(Normalise(cell.GetString()), out var key) && !map.ContainsKey(key)) map[key] = cell.Address.ColumnNumber;
            }
            return map;
        }

        private static string CellText(IXLCell cell, bool hsCode)
        {
            var value = cell.Value;
            if (value.IsBlank) return string.Empty;
            if (value.IsNumber)
            {
                double n = value.GetNumber();
                string digits = n.ToString("0.############", CultureInfo.InvariantCulture);
                // A code typed into a number cell loses its leading zero ("1012100" for "01012100").
                if (hsCode && digits.All(char.IsDigit) && (digits.Length == 7 || digits.Length == 3)) digits = "0" + digits;
                return digits;
            }
            if (value.IsDateTime) return value.GetDateTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (value.IsBoolean) return value.GetBoolean() ? "Yes" : "No";
            return cell.GetString().Trim();
        }

        private static decimal? Number(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string cleaned = Regex.Replace(text, @"[^\d.\-]", "");
            return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;
        }

        private static ExcelLine ReadLine(int row, Func<string, string> text, List<Tax> taxes, List<string> problems, IXLCell? hsCell)
        {
            var line = new ExcelLine { Row = row, Description = text("desc"), HsCode = text("hs").Replace(" ", "").Replace(".", "") };
            if (string.IsNullOrWhiteSpace(line.Description)) problems.Add($"Row {row}: 'Description' is empty.");

            var qty = Number(text("qty"));
            if (qty is null || qty <= 0) problems.Add($"Row {row}: 'Quantity' must be a number greater than 0.");
            else line.Quantity = qty.Value;

            var price = Number(text("price"));
            if (price is null || price < 0) problems.Add($"Row {row}: 'Unit price' must be a number, 0 or more.");
            else line.UnitPrice = price.Value;

            if (!string.IsNullOrWhiteSpace(text("discount")))
            {
                var discount = Number(text("discount"));
                if (discount is null || discount < 0) problems.Add($"Row {row}: 'Discount' must be an amount, 0 or more.");
                else line.Discount = discount.Value;
            }

            var tax = MatchTax(text("tax"), taxes);
            if (tax == null)
            {
                problems.Add(string.IsNullOrWhiteSpace(text("tax"))
                    ? $"Row {row}: 'Tax' is empty. Pick one from the list: {string.Join(", ", taxes.Select(TaxLabel))}."
                    : $"Row {row}: tax \"{text("tax")}\" is not one ZIMRA set for this device. Pick one from the list: {string.Join(", ", taxes.Select(TaxLabel))}.");
            }
            else
            {
                line.TaxID = tax.TaxID;
                line.TaxName = tax.TaxName;
                line.TaxPercent = tax.TaxPercent;
            }
            return line;
        }

        private static Tax? MatchTax(string text, List<Tax> taxes)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var id = Regex.Match(text, @"^\s*(\d+)\s*[-–—|:]");
            if (id.Success) return taxes.FirstOrDefault(t => t.TaxID == int.Parse(id.Groups[1].Value));
            var byName = taxes.Where(t => string.Equals(t.TaxName?.Trim(), text.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (byName.Count == 1) return byName[0];
            if (Regex.IsMatch(text.Trim(), @"^exempt", RegexOptions.IgnoreCase))
            {
                var exempt = taxes.Where(t => t.TaxPercent == null).ToList();
                return exempt.Count == 1 ? exempt[0] : null;
            }
            if (Number(text) is { } rate)
            {
                if (rate > 0 && rate < 1 && !text.Contains('%')) rate *= 100;   // a cell formatted as a percentage
                var byRate = taxes.Where(t => t.TaxPercent == rate).ToList();
                return byRate.Count == 1 ? byRate[0] : null;
            }
            return null;
        }

        private static void Finish(ExcelDocument doc, Device device)
        {
            string row = doc.Rows.Count > 0 ? $"Row {doc.Rows[0]}" : "Document";
            switch (Normalise(doc.TypeText ?? ""))
            {
                case "" or "invoice" or "taxinvoice" or "fiscalinvoice" or "fiscaltaxinvoice": doc.Type = ReceiptType.FiscalInvoice; break;
                case "creditnote" or "credit": doc.Type = ReceiptType.CreditNote; break;
                case "debitnote" or "debit": doc.Type = ReceiptType.DebitNote; break;
                default: doc.Problems.Add($"{row}: 'Type' must be Invoice, Credit note or Debit note, not \"{doc.TypeText}\"."); break;
            }

            if (string.IsNullOrWhiteSpace(doc.Currency)) doc.Problems.Add($"{row}: 'Currency' is empty. Use USD or ZWG.");
            else if (doc.Currency is not ("USD" or "ZWG")) doc.Problems.Add($"{row}: currency {doc.Currency} is not accepted. Use USD or ZWG.");

            switch (Normalise(doc.InclusiveText ?? ""))
            {
                case "" or "no" or "n" or "false" or "0" or "exclusive": doc.TaxInclusive = false; break;
                case "yes" or "y" or "true" or "1" or "inclusive": doc.TaxInclusive = true; break;
                default: doc.Problems.Add($"{row}: 'Prices include tax' must be Yes or No."); break;
            }

            if (!string.IsNullOrWhiteSpace(doc.PaidText))
            {
                string paid = Normalise(Regex.Replace(doc.PaidText, @"^\s*\d+\s*[-|:]\s*", ""));
                var match = Payments.FirstOrDefault(p => Normalise(p.name) == paid || Normalise(p.type.ToString()) == paid);
                if (match.name == null) doc.Problems.Add($"{row}: 'Paid by' must be one of {string.Join(", ", Payments.Select(p => p.name))}.");
                else doc.MoneyType = match.type;
            }
            else if (device.DefaultMoneyType == null)
            {
                doc.Problems.Add($"{row}: 'Paid by' is empty and the device has no default payment type. Say how it was paid.");
            }

            if (doc.Type != ReceiptType.FiscalInvoice)
            {
                string name = doc.Type == ReceiptType.CreditNote ? "credit note" : "debit note";
                if (string.IsNullOrWhiteSpace(doc.OriginalReference)) doc.Problems.Add($"{row}: a {name} needs the invoice it corrects in 'Original invoice no.'.");
                if (string.IsNullOrWhiteSpace(doc.Reason)) doc.Problems.Add($"{row}: a {name} needs a 'Reason'.");
            }
            else if (!string.IsNullOrWhiteSpace(doc.OriginalReference))
            {
                doc.Problems.Add($"{row}: 'Original invoice no.' is only for credit and debit notes. Set 'Type' to Credit note or Debit note, or clear it.");
            }
        }

        // ------------------------------------------------------------------ results

        /// <summary>The uploaded workbook with the result columns filled in for every row of each document.</summary>
        public static byte[] WriteResults(byte[] original, IReadOnlyDictionary<string, ExcelResult> results)
        {
            using var wb = new XLWorkbook(new MemoryStream(original));
            var ws = wb.Worksheets.FirstOrDefault(w => string.Equals(w.Name, SheetName, StringComparison.OrdinalIgnoreCase)) ?? wb.Worksheets.First();
            var map = HeaderMap(ws);
            int next = (ws.LastColumnUsed()?.ColumnNumber() ?? 0) + 1;
            foreach (var key in new[] { "r_fiscal", "r_status", "r_code", "r_qr", "r_message" })
            {
                if (map.ContainsKey(key)) continue;
                var col = Columns.First(c => c.Key == key);
                ws.Cell(1, next).Value = col.Header;
                ws.Cell(1, next).Style.Font.Bold = true;
                map[key] = next++;
            }

            int last = ws.LastRowUsed()?.RowNumber() ?? 1;
            for (int row = 2; row <= last; row++)
            {
                string reference = map.TryGetValue("ref", out int rc) ? CellText(ws.Cell(row, rc), false).ToLowerInvariant() : string.Empty;
                if (reference.Length == 0 || !results.TryGetValue(reference, out var r)) continue;
                ws.Cell(row, map["r_fiscal"]).Value = r.FiscalNumber;
                ws.Cell(row, map["r_status"]).Value = r.Status;
                ws.Cell(row, map["r_code"]).Value = r.VerificationCode;
                ws.Cell(row, map["r_qr"]).Value = r.QrLink;
                if (!string.IsNullOrEmpty(r.QrLink) && Uri.TryCreate(r.QrLink, UriKind.Absolute, out var uri)) ws.Cell(row, map["r_qr"]).SetHyperlink(new XLHyperlink(uri));
                ws.Cell(row, map["r_message"]).Value = r.Message;
                var fill = r.Status switch { "APPROVED" => "#D9EAD3", "REJECTED" or "NOT SENT" => "#F4CCCC", _ => "#FFF2CC" };
                ws.Range(row, map["r_fiscal"], row, map["r_message"]).Style.Fill.BackgroundColor = XLColor.FromHtml(fill);
            }

            using var stream = new MemoryStream();
            wb.SaveAs(stream);
            return stream.ToArray();
        }
    }
}
