using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ZimraEGS.ApiClient.Enums;
using ZimraEGS.Data;
using ZimraEGS.Models;

namespace ZimraEGS.Services
{
    /// <summary>What checking a document found: ready to send, already sent, or what to fix.</summary>
    public class ExcelCheck
    {
        public ExcelDocument Document { get; set; } = new();
        public decimal? Total { get; set; }
        public List<string> Problems { get; set; } = [];
        /// <summary>Sent before (same invoice number): the stored receipt.</summary>
        public FiscalReceipt? AlreadySent { get; set; }
        /// <summary>A credit or debit note whose invoice is in the same workbook: it is sent after that invoice.</summary>
        public string? AfterInvoice { get; set; }
        public bool Ready => Problems.Count == 0 && AlreadySent == null;
    }

    /// <summary>Checks and fiscalises the documents of an uploaded workbook through the same pipeline as Manager.</summary>
    public class ExcelService
    {
        private readonly FiscalDbContext _db;
        private readonly FiscalService _fiscal;

        public ExcelService(FiscalDbContext db, FiscalService fiscal)
        {
            _db = db;
            _fiscal = fiscal;
        }

        /// <summary>The fiscalised invoice a credit or debit note refers to: by fiscal number or by the customer's own number.</summary>
        public Task<FiscalReceipt?> FindOriginalAsync(int deviceRowId, string reference)
        {
            string trimmed = reference.Trim();
            string formKey = ExcelDocument.FormKeyFor(trimmed);
            return _db.Receipts.AsNoTracking()
                .Where(r => r.DeviceRowId == deviceRowId && r.ReceiptType == ReceiptType.FiscalInvoice && r.Status == ReceiptStatus.Approved
                    && (r.FormKey == formKey || r.InvoiceNo == trimmed))
                .OrderByDescending(r => r.Id)
                .FirstOrDefaultAsync();
        }

        public async Task<List<ExcelCheck>> CheckAsync(Device device, List<ExcelDocument> documents)
        {
            var checks = new List<ExcelCheck>();
            foreach (var doc in documents)
            {
                var check = new ExcelCheck { Document = doc, Problems = [.. doc.Problems] };
                checks.Add(check);

                check.AlreadySent = await _fiscal.FindRecordedReceiptAsync(device.Id, ExcelDocument.FormKeyFor(doc.Reference));
                if (check.AlreadySent != null) { check.Total = check.AlreadySent.Total; continue; }
                if (check.Problems.Count > 0) continue;

                var (relay, originalProblem) = await RelayAsync(device, doc);
                if (relay == null)
                {
                    // The invoice is in this workbook too: check the note against it once it has been sent.
                    var sameBatch = documents.FirstOrDefault(d => d.Type == ReceiptType.FiscalInvoice
                        && string.Equals(d.Reference.Trim(), doc.OriginalReference.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (sameBatch != null && sameBatch.Problems.Count == 0) { check.AfterInvoice = sameBatch.Reference; continue; }
                    check.Problems.Add(originalProblem!);
                    continue;
                }

                var draft = await _fiscal.PreviewAsync(device.Id, relay);
                check.Total = draft.Receipt.ReceiptTotal;
                check.Problems.AddRange(draft.Problems.Select(p => InExcelTerms(p, doc)));
            }
            return checks;
        }

        private async Task<(RelayData? relay, string? problem)> RelayAsync(Device device, ExcelDocument doc)
        {
            FiscalReceipt? original = null;
            if (doc.Type != ReceiptType.FiscalInvoice)
            {
                original = await FindOriginalAsync(device.Id, doc.OriginalReference);
                if (original == null)
                {
                    return (null, $"Row {doc.Rows[0]}: no invoice \"{doc.OriginalReference}\" was fiscalised on this device. 'Original invoice no.' must be an invoice already sent to ZIMRA, by its fiscal number or your own number.");
                }
            }
            return (RelayData.FromSpreadsheet(doc, device, original), null);
        }

        /// <summary>The checks are shared with Manager; say "row 12" instead of "line 2" and name the Excel columns.</summary>
        private static string InExcelTerms(string problem, ExcelDocument doc)
        {
            problem = Regex.Replace(problem, @"\bLine (\d+)", m =>
            {
                int n = int.Parse(m.Groups[1].Value);
                return n >= 1 && n <= doc.Lines.Count ? $"Row {doc.Lines[n - 1].Row}" : m.Value;
            });
            return problem
                .Replace(" Set it on the inventory item or the line.", " Fill in 'HS code'.")
                .Replace("Complete the customer's details or remove both.", "Fill in 'Customer TIN' or clear 'Customer name'.")
                .Replace("Choose a Payment Type on the invoice, or set a default payment type below so you never have to.", "Fill in 'Paid by'.");
        }

        /// <summary>
        /// Fiscalises the documents that are ready, in order. Stops when ZIMRA cannot be reached or the subscription
        /// does not allow sending; nothing is lost, and documents already sent are never sent twice.
        /// </summary>
        public async Task<Dictionary<string, ExcelResult>> SendAsync(Device device, List<ExcelDocument> documents)
        {
            var results = new Dictionary<string, ExcelResult>();
            var checks = await CheckAsync(device, documents);
            string? stopped = null;

            // Invoices first, so notes against an invoice in the same workbook find it.
            foreach (var check in checks.OrderBy(c => c.Document.Type == ReceiptType.FiscalInvoice ? 0 : 1))
            {
                string key = check.Document.Reference.Trim().ToLowerInvariant();
                if (check.AlreadySent != null) { results[key] = FromReceipt(check.AlreadySent, "Sent before; not sent again."); continue; }
                if (check.Problems.Count > 0) { results[key] = NotSent(string.Join(" ", check.Problems)); continue; }
                if (stopped != null) { results[key] = NotSent(stopped); continue; }

                var (relay, problem) = await RelayAsync(device, check.Document);
                if (relay == null) { results[key] = NotSent(problem!); continue; }

                var result = await _fiscal.FiscaliseAsync(device.Id, relay);
                if (result.Success)
                {
                    var r = result.Receipt!;
                    results[key] = FromReceipt(r, r.Status == ReceiptStatus.Approved ? "" : "ZIMRA recorded it with validation errors.");
                    continue;
                }

                string message = string.Join(" ", new[] { result.ProblemTitle }.Concat(result.Problems).Where(x => !string.IsNullOrWhiteSpace(x)));
                results[key] = NotSent(message);
                bool stopAll = result.ProblemTitle is { } t && (t.Contains("ZIMRA could not be reached") || t.Contains("did not answer")
                    || t.Contains("not available") || t.Contains("subscription") || t.Contains("not linked") || t.Contains("unfinished") || t.Contains("pending"));
                if (stopAll) stopped = $"Not sent because of the problem above ({result.ProblemTitle}). Upload the workbook again later: documents already sent are not sent twice.";
            }
            foreach (var check in checks)
            {
                if (results.TryGetValue(check.Document.Reference.Trim().ToLowerInvariant(), out var r))
                {
                    r.Reference = check.Document.Reference;
                    r.Type = check.Document.TypeName;
                }
            }
            return results;
        }

        private static ExcelResult NotSent(string message) => new() { Status = "NOT SENT", Message = message };

        private static ExcelResult FromReceipt(FiscalReceipt r, string message) => new()
        {
            FiscalNumber = r.InvoiceNo,
            Status = r.Status,
            VerificationCode = r.VerificationCode,
            QrLink = r.QrCode,
            Message = message,
            ReceiptRowId = r.Id,
        };
    }
}
