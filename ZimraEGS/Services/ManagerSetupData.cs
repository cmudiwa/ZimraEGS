using Newtonsoft.Json.Linq;
using System.Security.Cryptography;
using System.Text;
using ZimraEGS.ApiClient.Models;
using ZimraEGS.Data;
using ZimraEGS.Helpers;
using ZimraEGS.Models;

namespace ZimraEGS.Services
{
    /// <summary>
    /// The records the setup wizard creates in Manager: custom fields, the invoice theme and tax codes.
    /// Tax codes come from the taxes ZIMRA configured for the device, so they always carry the right tax IDs.
    /// </summary>
    public static class ManagerSetupData
    {
        // Keys of the tax codes earlier versions created; reused so running setup again updates them.
        private static readonly Dictionary<int, string> KnownTaxCodeKeys = new()
        {
            [1] = "6d0a93d1-a251-4509-94d2-9c1b2ade93b3",
            [2] = "693aa908-e2ae-4a4a-997f-b23cc056d7fd",
            [3] = "d45ffe42-182a-4920-a5c6-dd2fd983794a",
            [514] = "51285cd8-0cfb-402c-9a3b-b1775cd16ce6",
        };

        // Custom fields that held device state in Manager before it moved to the server.
        private static readonly string[] ObsoleteFieldKeys =
        [
            ManagerCustomField.BusinessReferenceGuid,
            ManagerCustomField.FiscalDaySummaryGuid,
        ];

        /// <summary>
        /// Increased whenever the records created in Manager change (fields, theme). A business set up with an
        /// older revision is brought up to date the next time its panel is opened.
        /// </summary>
        public const int Revision = 3;

        public const string ThemeKey = "f43edcbb-2448-4890-bbd2-18f87fdbad13";

        /// <summary>Written to the business so the server can tell which revision it was set up with.</summary>
        public static string SetupMarker => $"{VersionHelper.GetVersion()} setup{Revision}";

        public static bool IsCurrent(string? marker) => marker != null && marker.EndsWith($" setup{Revision}", StringComparison.Ordinal);

        // Fields on the business that only the server uses. They must not show on views or printed documents:
        // the device link is a secret.
        private static readonly string[] HiddenFieldKeys =
        [
            ManagerCustomField.CertificateInfoGuid,
            ManagerCustomField.AppVersionGuid,
        ];

        public static string TaxCodeKey(int taxID)
        {
            if (KnownTaxCodeKeys.TryGetValue(taxID, out var key)) return key;
            // Stable key per tax ID, so repeating setup never creates duplicates.
            return new Guid(MD5.HashData(Encoding.UTF8.GetBytes("zimraegs-tax-code-" + taxID))).ToString();
        }

        public static JObject Build(Device? device)
        {
            var data = JObject.Parse(Utils.ReadEmbeddedResource("cfData.json"));

            if (data["text-custom-field-form"] is JArray textFields)
            {
                foreach (var field in textFields.Where(f => ObsoleteFieldKeys.Contains((string?)f["Key"])).ToList())
                {
                    field.Remove();
                }
            }

            if (data["text-custom-field-form"] is JArray shownFields)
            {
                foreach (var field in shownFields.OfType<JObject>().Where(f => HiddenFieldKeys.Contains((string?)f["Key"])))
                {
                    field["DisplayOnView"] = false;
                    field["LockedForManualEditing"] = true;
                    field.Remove("Description");
                }
            }

            // The printed title of a fiscal document, set at fiscalisation and read by the invoice theme.
            if (data["text-custom-field-form"] is JArray textFieldList &&
                textFieldList.OfType<JObject>().FirstOrDefault(f => (string?)f["Key"] == ManagerCustomField.ApprovalStatusGuid) is JObject statusField &&
                !textFieldList.Any(f => (string?)f["Key"] == ManagerCustomField.FiscalDocumentGuid))
            {
                textFieldList.Add(new JObject
                {
                    ["DisplayOnView"] = true,
                    ["Key"] = ManagerCustomField.FiscalDocumentGuid,
                    ["Name"] = "Fiscal Document",
                    ["Placement"] = statusField["Placement"]?.DeepClone(),
                    ["Position"] = 89,
                    ["Size"] = "Medium",
                    ["LockedForManualEditing"] = true,
                    ["ExcludeFromCopyingOrCloning"] = true,
                });
            }

            // The invoice theme in ZIMRA's layout. Manager themes are complete HTML pages.
            if (data["theme-form"] is JArray themes && themes.FirstOrDefault() is JObject theme)
            {
                theme["Key"] = ThemeKey;
                theme["Template"] = Utils.ReadEmbeddedResource("zimra-invoice.html");
            }

            var taxes = device == null ? null : DeviceStore.GetConfig(device)?.ApplicableTaxes;
            if (taxes != null && taxes.Count > 0)
            {
                var now = TimeZoneHelper.GetSouthAfricaTime();
                var taxCodes = new JArray();
                foreach (var tax in taxes.Where(t => t.ValidTill == null || t.ValidTill.Value.AddDays(1) > now).GroupBy(t => t.TaxID).Select(g => g.Last()).OrderBy(t => t.TaxID))
                {
                    taxCodes.Add(TaxCode(tax));
                }
                data["tax-code-form"] = taxCodes;
                PointSampleItemsAtTaxCodes(data, taxCodes);
            }

            // Sample items, customers and currency are only useful while trying things out on the test environment.
            if (device != null && device.Environment == PlatformType.Production)
            {
                data.Remove("inventory-item-form");
                data.Remove("customer-form");
                data.Remove("foreign-currency-form");
            }

            return data;
        }

        // Manager's current API takes enum values as numbers.
        private static readonly Dictionary<string, Dictionary<string, int>> EnumValues = new()
        {
            ["text-custom-field-form/Type"] = new() { ["SingleLineText"] = 0, ["ParagraphText"] = 1, ["DropdownList"] = 2 },
            ["text-custom-field-form/Size"] = new() { ["Small"] = 0, ["Medium"] = 1, ["Large"] = 2 },
            ["tax-code-form/TaxRate"] = new() { ["ZeroRate"] = 0, ["TotalRate"] = 1, ["CustomRate"] = 2 },
            ["tax-code-form/Type"] = new() { ["SingleRate"] = 0, ["MultipleRates"] = 1 },
            ["inventory-item-form/ValuationMethod"] = new() { ["FirstInFirstOut"] = 0, ["WeightedAverageCost"] = 1, ["Manual"] = 2 },
        };

        private static readonly Dictionary<string, string> Api3FormNames = new()
        {
            ["theme-form"] = "custom-theme-form",
        };

        /// <summary>
        /// The same records as <see cref="Build"/>, as the requests the extension page sends through
        /// Manager's API bridge: PUT /api3/{form}/{key} with camelCase bodies.
        /// </summary>
        public static List<ManagerApiCall> BuildApiCalls(Device? device)
        {
            var calls = new List<ManagerApiCall>();

            foreach (var form in Build(device).Properties())
            {
                if (form.Value is not JArray items) continue;
                string formName = Api3FormNames.GetValueOrDefault(form.Name, form.Name);

                foreach (var item in items.OfType<JObject>())
                {
                    string? key = (string?)item["Key"];
                    if (string.IsNullOrEmpty(key)) continue;

                    var body = (JObject)item.DeepClone();
                    body.Remove("Key");
                    foreach (var property in body.Properties().ToList())
                    {
                        if (property.Value.Type == JTokenType.String &&
                            EnumValues.TryGetValue($"{form.Name}/{property.Name}", out var map) &&
                            map.TryGetValue((string)property.Value!, out int number))
                        {
                            property.Value = number;
                        }
                    }

                    calls.Add(new ManagerApiCall
                    {
                        Method = "PUT",
                        ApiUrl = $"/api3/{formName}/{key}",
                        Body = ManagerApiJson.ToApiShape(body),
                    });
                }
            }

            return calls;
        }

        /// <summary>The sample items must use tax codes that exist for this device, or they would carry no tax.</summary>
        private static void PointSampleItemsAtTaxCodes(JObject data, JArray taxCodes)
        {
            if (data["inventory-item-form"] is not JArray items) return;

            var codes = taxCodes.OfType<JObject>().ToList();
            JObject? exempt = codes.FirstOrDefault(c => ((string?)c["Name"] ?? "").Contains("Exempt", StringComparison.OrdinalIgnoreCase));
            JObject? zero = codes.FirstOrDefault(c => ((string?)c["Name"] ?? "").Contains("Zero", StringComparison.OrdinalIgnoreCase));
            JObject? standard = codes.Where(c => (decimal?)c["Rate"] > 0 && !((string?)c["Name"] ?? "").Contains("Withholding", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(c => (decimal?)c["Rate"]).FirstOrDefault();

            foreach (var item in items.OfType<JObject>())
            {
                string name = (string?)item["ItemName"] ?? string.Empty;
                JObject? code = name.Contains("Exempt") ? exempt : name.Contains("Zero") ? zero : standard;

                if (code == null)
                {
                    item["HasDefaultTaxCode"] = false;
                    item.Remove("DefaultTaxCode");
                    continue;
                }

                item["HasDefaultTaxCode"] = true;
                item["DefaultTaxCode"] = code["Key"];
                if (ReferenceEquals(code, standard))
                {
                    item["ItemName"] = "Item standard rate";
                    item["DefaultLineDescription"] = "Item with standard rate VAT";
                }
            }
        }

        /// <summary>
        /// Brings a business that was set up with an older revision up to date: custom fields and the invoice
        /// theme. Sample records and tax codes are left alone; the user may have changed or removed them.
        /// </summary>
        public static List<ManagerApiCall> BuildUpgradeCalls(Device device)
        {
            string[] forms = ["/api3/text-custom-field-form/", "/api3/number-custom-field-form/", "/api3/custom-theme-form/"];
            return BuildApiCalls(device).Where(c => forms.Any(f => c.ApiUrl.StartsWith(f, StringComparison.Ordinal))).ToList();
        }

        private static JObject TaxCode(Tax tax)
        {
            return new JObject
            {
                ["Key"] = TaxCodeKey(tax.TaxID),
                ["Label"] = tax.TaxName,
                // The number after the dash is the ZIMRA tax ID the receipt is reported under.
                ["Name"] = $"{tax.TaxName} — {tax.TaxID}",
                ["Rate"] = tax.TaxPercent ?? 0,
                ["TaxRate"] = "CustomRate",
                ["Type"] = "SingleRate",
            };
        }
    }
}
