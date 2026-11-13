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
