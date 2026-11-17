using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ZimraEGS.ApiClient.Enums;
using ZimraEGS.Data;
using ZimraEGS.Helpers;
using ZimraEGS.Models;

namespace ZimraEGS.Services
{
    /// <summary>
    /// Prints fiscal documents sent from Excel with the same ZIMRA layout theme that Manager uses, so both routes
    /// produce identical documents. The theme is given the data Manager would give it, built from the stored receipt.
    /// </summary>
    public static class ExcelPrint
    {
        /// <summary>The document as Manager's view API describes it, which the theme renders.</summary>
        public static JObject ViewData(Device device, FiscalReceipt row, string customerReference)
        {
            var info = DeviceStore.GetInfo(device);
            if (string.IsNullOrWhiteSpace(info.TaxPayerName)) info.TaxPayerName = DeviceStore.GetConfig(device)?.TaxPayerName ?? string.Empty;
            var receiptDate = TimeZoneHelper.ParseFdmsString(row.ReceiptDate);
            string notes = string.Empty;
            try { notes = FiscalService.ParseReceipt(row).ReceiptNotes ?? string.Empty; } catch (JsonException) { }

            var fields = new JArray();
            void Add(string key, object? value)
            {
                string text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                if (text.Length > 0) fields.Add(new JObject { ["key"] = key, ["label"] = "", ["text"] = text });
            }

            Add(ManagerCustomField.ApprovalStatusGuid, row.Status);
            Add(ManagerCustomField.FiscalDocumentGuid, ManagerWriteBack.FiscalDocumentTitle(device, row.ReceiptType));
            Add(ManagerCustomField.FiscalBuyerGuid, ManagerWriteBack.BuyerText(row));
            Add(ManagerCustomField.FiscalLinesGuid, ManagerWriteBack.FiscalLinesJson(device, row));
            Add(ManagerCustomField.ReceiptQRCodeGuid, row.QrCode);
            Add(ManagerCustomField.VerificationCodeGuid, row.VerificationCode);
            Add(ManagerCustomField.ReceiptDateGuid, receiptDate?.ToString("yyyy-MM-dd HH:mm:ss"));
            Add(ManagerCustomField.DeviceIDGuid, device.DeviceID);
            Add(ManagerCustomField.DeviceSNGuid, device.SerialNumber);
            Add(ManagerCustomField.FiscalDayNoGuid, row.FiscalDayNo);
            Add(ManagerCustomField.ReceiptGlobalNoGuid, row.ReceiptGlobalNo);
            Add(ManagerCustomField.ReceiptCounterGuid, row.ReceiptCounter);

            if (row.ReceiptType != ReceiptType.FiscalInvoice && !string.IsNullOrEmpty(row.RefJson))
            {
                var r = JsonConvert.DeserializeObject<ReceiptReferenceInfo>(row.RefJson)!;
                Add(ManagerCustomField.ReceiptNotesGuid, notes);
                Add(ManagerCustomField.ReceiptNoRefGuid, r.ReceiptRefNo);
                Add(ManagerCustomField.ReceiptRefDateGuid, r.ReceiptRefDate);
                Add(ManagerCustomField.DeviceIDRefGuid, r.DeviceIDRef);
                Add(ManagerCustomField.DeviceSNRefGuid, r.DeviceSNRef);
                Add(ManagerCustomField.FiscalDayNoRefGuid, r.FiscalDayNoRef);
                Add(ManagerCustomField.ReceiptCounterRefGuid, r.ReceiptCounterRef);
                Add(ManagerCustomField.ReceiptGlobalNoRefGuid, r.ReceiptGlobalNoRef);
            }

            return new JObject
            {
                ["title"] = ManagerWriteBack.FiscalDocumentTitle(device, row.ReceiptType),
                ["reference"] = customerReference,
                ["businessName"] = info.TaxPayerName,
                ["business"] = new JObject { ["name"] = info.TaxPayerName, ["address"] = info.ToBusinessAddress() },
                ["fields"] = fields,
            };
        }

        /// <summary>A page with one or more documents, one per printed page.</summary>
        public static string Page(IEnumerable<JObject> documents)
        {
            string theme = Utils.ReadEmbeddedResource("zimra-invoice.html");
            string data = new JArray(documents).ToString(Formatting.None).Replace("</", "<\\/");

            theme = Replace(theme, "<script src=\"/resources/qrcode/qrcode.js\"></script>", "<script src=\"/js/qrcode.js\"></script>");
            theme = Replace(theme, "const main = document.querySelector('main');", "const main = window.__main || document.querySelector('main');");
            theme = Replace(theme, "<main></main>",
                "<div class=\"screen-only toolbar\"><button type=\"button\" onclick=\"window.print()\">Print or save as PDF</button></div>\n<main></main>");
            theme = Replace(theme, "</style>", @"body { flex-direction: column; align-items: center; }
.toolbar { margin: 1rem 0 0; }
.toolbar button { font: 14px Arial, sans-serif; padding: 8px 16px; border-radius: 6px; border: 1px solid #b71c1c; background: #b71c1c; color: #fff; cursor: pointer; }
@media print { main ~ main { break-before: page; } }
</style>");

            int fetchAt = theme.IndexOf("fetch('/api4/view-v1'", StringComparison.Ordinal);
            int scriptEnd = theme.IndexOf("</script>", fetchAt, StringComparison.Ordinal);
            if (fetchAt < 0 || scriptEnd < 0) throw new InvalidOperationException("The invoice theme has changed; update ExcelPrint.");
            string loader = @"(function () {
  const docs = JSON.parse(document.getElementById('fiscal-documents').textContent);
  const first = document.querySelector('main');
  docs.forEach(function (d, i) {
    window.__main = i === 0 ? first : document.body.appendChild(document.createElement('main'));
    renderDocument(d);
  });
  if (docs.length > 1) document.title = docs.length + ' fiscal documents';
})();
";
            theme = theme[..fetchAt] + loader + theme[scriptEnd..];
            return Replace(theme, "<body>", "<body>\n<script type=\"application/json\" id=\"fiscal-documents\">" + data + "</script>");
        }

        private static string Replace(string text, string find, string with)
        {
            int at = text.IndexOf(find, StringComparison.Ordinal);
            if (at < 0) throw new InvalidOperationException("The invoice theme has changed; update ExcelPrint: " + find);
            return text[..at] + with + text[(at + find.Length)..];
        }
    }
}
