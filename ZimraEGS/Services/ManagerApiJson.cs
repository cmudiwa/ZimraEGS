using Newtonsoft.Json.Linq;

namespace ZimraEGS.Services
{
    /// <summary>
    /// Converts between the JSON of Manager's current API (camelCase names, numeric enums, references as keys)
    /// and the shape the rest of this application works with (the names of Manager's data model).
    /// </summary>
    public static class ManagerApiJson
    {
        private static bool IsGuid(string name) => Guid.TryParse(name, out _);

        private static string Upper(string name) => name.Length == 0 || char.IsUpper(name[0]) ? name : char.ToUpperInvariant(name[0]) + name[1..];
        private static string Lower(string name) => name.Length == 0 || char.IsLower(name[0]) ? name : char.ToLowerInvariant(name[0]) + name[1..];

        private static JToken Rename(JToken token, Func<string, string> rename)
        {
            switch (token)
            {
                case JObject obj:
                    var result = new JObject();
                    foreach (var property in obj.Properties())
                    {
                        // Custom field values are keyed by the field's GUID, which is not a name.
                        string name = IsGuid(property.Name) ? property.Name : rename(property.Name);
                        result[name] = Rename(property.Value, rename);
                    }
                    return result;
                case JArray array:
                    return new JArray(array.Select(item => Rename(item, rename)));
                default:
                    return token.DeepClone();
            }
        }

        /// <summary>camelCase names to the model's names (issueDate to IssueDate).</summary>
        public static JObject ToRelayShape(JObject apiObject) => (JObject)Rename(apiObject, Upper);

        /// <summary>Model names to the API's camelCase names.</summary>
        public static JObject ToApiShape(JObject modelObject) => (JObject)Rename(modelObject, Lower);

        private static decimal Number(JToken? token)
        {
            if (token == null || token.Type == JTokenType.Null) return 0;
            return token.Type is JTokenType.Integer or JTokenType.Float ? token.Value<decimal>()
                : decimal.TryParse(token.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
        }

        /// <summary>
        /// Prepares a sales invoice or credit note read from Manager's API for receipt building:
        /// model names, the customer as the invoice party, the original invoice as the referenced invoice,
        /// and per line a quantity, unit price, discount amount and tax rate that are always filled in.
        /// </summary>
        public static JObject ToRelayDocument(JObject apiDocument)
        {
            var doc = ToRelayShape(apiDocument);

            Move(doc, "Customer", "InvoiceParty");
            Move(doc, "SalesInvoice", "RefInvoice");

            // A reference that could not be looked up stays a bare key; drop it so it is not mistaken for data.
            foreach (string name in new[] { "InvoiceParty", "RefInvoice" })
            {
                if (doc[name] != null && doc[name]!.Type != JTokenType.Object) doc.Remove(name);
            }

            if (doc["RefInvoice"] is JObject referenced)
            {
                // Only the fiscal details of the original invoice matter; its lines must not be confused with ours.
                referenced.Remove("Lines");
                referenced.Remove("Customer");
            }

            bool hasDiscount = doc.Value<bool?>("Discount") ?? false;
            // Manager: 0 = percentage, 1 = exact amount.
            bool percentage = (int)Number(doc["DiscountType"]) == 0;

            if (doc["Lines"] is JArray lines)
            {
                foreach (var line in lines.OfType<JObject>())
                {
                    // An empty quantity means one.
                    decimal qty = line["Qty"] == null || line["Qty"]!.Type == JTokenType.Null ? 1 : Number(line["Qty"]);
                    decimal price = Number(line["SalesUnitPrice"]);
                    line["Qty"] = qty;
                    line["UnitPrice"] = price;
                    line.Remove("SalesUnitPrice");

                    decimal discount = 0;
                    if (hasDiscount)
                    {
                        discount = percentage
                            ? decimal.Round(qty * price * Number(line["DiscountPercentage"]) / 100m, 2, MidpointRounding.AwayFromZero)
                            : Number(line["DiscountAmount"]);
                    }
                    line["DiscountAmount"] = discount;

                    foreach (string name in new[] { "Item", "TaxCode" })
                    {
                        if (line[name] != null && line[name]!.Type != JTokenType.Object) line.Remove(name);
                    }

                    if (line["TaxCode"] is JObject taxCode)
                    {
                        taxCode["Rate"] = TaxRate(taxCode);
                    }
                }
            }

            return doc;
        }

        /// <summary>The percentage a Manager tax code charges: 0 for zero rate, the sum of the parts for multiple rates.</summary>
        private static decimal TaxRate(JObject taxCode)
        {
            // Manager: TaxRate 0 = zero rate, 1 = total rate, 2 = custom rate; Type 0 = single, 1 = multiple.
            var taxRate = taxCode["TaxRate"];
            if (taxRate != null && taxRate.Type == JTokenType.Integer && taxRate.Value<int>() == 0) return 0;

            var type = taxCode["Type"];
            if (type != null && type.Type == JTokenType.Integer && type.Value<int>() == 1 && taxCode["Components"] is JArray components)
            {
                return components.OfType<JObject>().Sum(c => Number(c["ComponentRate"]));
            }

            return Number(taxCode["Rate"]);
        }

        private static void Move(JObject obj, string from, string to)
        {
            if (obj[from] == null) return;
            obj[to] = obj[from];
            obj.Remove(from);
        }
    }
}
