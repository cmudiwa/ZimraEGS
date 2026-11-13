using Newtonsoft.Json;
using ZimraEGS.ApiClient.Enums;
using ZimraEGS.ApiClient.Helpers;
using ZimraEGS.ApiClient.Models;
using ZimraEGS.Helpers;
using ZimraEGS.Services;

namespace ZimraEGS.Models
{
    public class RelayData
    {
        public string Referrer { get; private set; } = string.Empty;
        public string FormKey { get; private set; } = string.Empty;
        public string Data { get; private set; } = string.Empty;
        public string Api { get; private set; } = string.Empty;
        public string Token { get; private set; } = string.Empty;

        public long EGSVersion { get; private set; }

        public decimal InvoiceTotal { get; private set; } = 0;
        public string ApprovalStatus { get; set; } = string.Empty;
        public int InvReceiptGlobalNo { get; private set; } = 0;

        public DateTimeOffset? ReceiptDate { get; private set; }

        public string BusinessDetailJson { get; set; } = string.Empty;
        public string InvoiceJson { get; set; } = string.Empty;

        // State kept in Manager custom fields by versions before the server-side database.
        // Only read to import an existing device; never written to any more.
        public BusinessReference? LegacyBusinessReference { get; private set; }
        public FiscalDaySummary? LegacyFiscalDaySummary { get; private set; }
        public CertificateInfo? LegacyCertificateInfo { get; private set; }

        /// <summary>Raw content of the device custom field: either a device link or a legacy certificate blob.</summary>
        public string DeviceLinkRaw { get; private set; } = string.Empty;

        public ReceiptType ReceiptType { get; private set; } = ReceiptType.FiscalInvoice;
        public string CurrencyCode { get; private set; } = string.Empty;
        public string ReceiptNotes { get; private set; } = string.Empty;
        public int DeviceIDRef { get; private set; }
        public string DeviceSNRef { get; private set; } = string.Empty;
        public int FiscalDayNoRef { get; private set; }
        public int ReceiptCounterRef { get; private set; }
        public int ReceiptGlobalNoRef { get; private set; }
        public string ReceiptRefNo { get; set; }

        [Newtonsoft.Json.JsonConverter(typeof(LocalDateTimeConverter))]
        public DateTimeOffset ReceiptRefDate { get; set; } = DateTimeOffset.MinValue;

        public string ReceiptPrintForm { get; private set; } = string.Empty;
        public string PaymentType1 { get; private set; } = string.Empty;
        public decimal PaymentAmount1 { get; private set; } = 0;
        public string PaymentType2 { get; private set; } = string.Empty;
        public decimal PaymentAmount2 { get; private set; } = 0;

        public string ReceiptQrCode { get; set; } = string.Empty;
        public string VerificationCode { get; set; } = string.Empty;
        /// <summary>FDMS receipt ID of the invoice a credit or debit note refers to. 0 when unknown.</summary>
        public long ReceiptID { get; set; }

        public string BuyerRegisterName { get; private set; } = string.Empty;
        public string BuyerTradeName { get; private set; } = string.Empty;
        public string BuyerTIN { get; private set; } = string.Empty;
        public string BuyerVatNumber { get; private set; } = string.Empty;
        public string BuyerEmail { get; private set; } = string.Empty;
        public string BuyerPhoneNo { get; private set; } = string.Empty;
        public string BuyerProvince { get; private set; } = string.Empty;
        public string BuyerCity { get; private set; } = string.Empty;
        public string BuyerStreet { get; private set; } = string.Empty;
        public string BuyerHouseNo { get; private set; } = string.Empty;

        public ManagerInvoice ManagerInvoice { get; private set; }
        public InvoiceReference? ReceiptReference { get; set; }

        public RelayData() { }
        public RelayData(Dictionary<string, string> formData)
        {
            Referrer = Utils.GetValue(formData, "Referrer");
            FormKey = Utils.GetValue(formData, "Key");
            Data = Utils.GetValue(formData, "Data");
            Api = Utils.GetValue(formData, "Api");
            Token = Utils.GetValue(formData, "Token");

            string invoiceView = Utils.GetValue(formData, "View");
            InvoiceTotal = (decimal)RelayDataHelper.ParseTotalValue(invoiceView);

            BusinessDetailJson = RelayDataHelper.GetValueJson(Data, "BusinessDetails");
            InitializeBusinessData(BusinessDetailJson);

            InvoiceJson = RelayDataHelper.FindStringValueInJson(Data, FormKey);

            var mergedJson = RelayDataHelper.GetJsonDataByGuid(Data, FormKey);
            if (string.IsNullOrEmpty(InvoiceJson) || string.IsNullOrEmpty(mergedJson))
            {
                throw new InvalidOperationException("The relayed data does not contain the invoice. Relay from the invoice or credit note view screen.");
            }
            ManagerInvoice = JsonConvert.DeserializeObject<ManagerInvoice>(mergedJson);

            InitializeInvoiceData(mergedJson);

            DetermineInvoiceType(mergedJson);
        }
        private void InitializeBusinessData(string jsonBusiness)
        {
            DeviceLinkRaw = RelayDataHelper.GetStringCustomField2Value(jsonBusiness, ManagerCustomField.CertificateInfoGuid) ?? string.Empty;

            if (!string.IsNullOrEmpty(DeviceLinkRaw) && !DeviceLink.IsLink(DeviceLinkRaw))
            {
                LegacyCertificateInfo = ObjectCompressor.DeserializeFromBase64String<CertificateInfo>(DeviceLinkRaw);

                string brf = RelayDataHelper.GetStringCustomField2Value(jsonBusiness, ManagerCustomField.BusinessReferenceGuid);
                if (!string.IsNullOrWhiteSpace(brf))
                {
                    try { LegacyBusinessReference = JsonConvert.DeserializeObject<BusinessReference>(brf); } catch (JsonException) { }
                }

                string fds = RelayDataHelper.GetStringCustomField2Value(jsonBusiness, ManagerCustomField.FiscalDaySummaryGuid);
                if (!string.IsNullOrWhiteSpace(fds))
                {
                    try { LegacyFiscalDaySummary = JsonConvert.DeserializeObject<FiscalDaySummary>(fds); } catch (JsonException) { }
                }
            }

            EGSVersion = VersionHelper.GetNumberOnly(RelayDataHelper.GetStringCustomField2Value(jsonBusiness, ManagerCustomField.AppVersionGuid));
        }

        private void InitializeInvoiceData(string jsonInvoice)
        {
            ApprovalStatus = RelayDataHelper.GetStringCustomField2Value(jsonInvoice, ManagerCustomField.ApprovalStatusGuid);

            if (!string.IsNullOrEmpty(ApprovalStatus))
            {
                InvReceiptGlobalNo = GetIntValue(jsonInvoice, ManagerCustomField.ReceiptGlobalNoGuid);
                ReceiptDate = GetDateTimeValue(jsonInvoice, ManagerCustomField.ReceiptDateGuid);

                ReceiptReference = ParseInvoiceReference(RelayDataHelper.GetStringCustomField2Value(jsonInvoice, ManagerCustomField.ServerResponseGuid));
            }

            PaymentType1 = RelayDataHelper.GetStringCustomField2Value(jsonInvoice, ManagerCustomField.PaymentType1Guid);
            PaymentAmount1 = GetDoubleValue(jsonInvoice, ManagerCustomField.PaymentAmount1Guid);

            PaymentType2 = RelayDataHelper.GetStringCustomField2Value(jsonInvoice, ManagerCustomField.PaymentType2Guid);
            PaymentAmount2 = GetDoubleValue(jsonInvoice, ManagerCustomField.PaymentAmount2Guid);

            BuyerRegisterName = RelayDataHelper.GetStringCustomField2Value(jsonInvoice, ManagerCustomField.BuyerRegisterNameGuid, "InvoiceParty");
            BuyerTradeName = RelayDataHelper.GetStringCustomField2Value(jsonInvoice, ManagerCustomField.BuyerTradeNameGuid, "InvoiceParty");
            BuyerTIN = RelayDataHelper.GetStringCustomField2Value(jsonInvoice, ManagerCustomField.BuyerTINGuid, "InvoiceParty");
            BuyerVatNumber = RelayDataHelper.GetStringCustomField2Value(jsonInvoice, ManagerCustomField.BuyerVatNumberGuid, "InvoiceParty");
            BuyerEmail = RelayDataHelper.GetStringCustomField2Value(jsonInvoice, ManagerCustomField.BuyerEmailGuid, "InvoiceParty");
            BuyerPhoneNo = RelayDataHelper.GetStringCustomField2Value(jsonInvoice, ManagerCustomField.BuyerPhoneNoGuid, "InvoiceParty");
            BuyerProvince = RelayDataHelper.GetStringCustomField2Value(jsonInvoice, ManagerCustomField.BuyerProvinceGuid, "InvoiceParty");
            BuyerCity = RelayDataHelper.GetStringCustomField2Value(jsonInvoice, ManagerCustomField.BuyerCityGuid, "InvoiceParty");
            BuyerStreet = RelayDataHelper.GetStringCustomField2Value(jsonInvoice, ManagerCustomField.BuyerStreetGuid, "InvoiceParty");
            BuyerHouseNo = RelayDataHelper.GetStringCustomField2Value(jsonInvoice, ManagerCustomField.BuyerHouseNoGuid, "InvoiceParty");

            ReceiptQrCode = RelayDataHelper.GetStringCustomField2Value(jsonInvoice, ManagerCustomField.ReceiptQRCodeGuid);
            VerificationCode = RelayDataHelper.GetStringCustomField2Value(jsonInvoice, ManagerCustomField.VerificationCodeGuid);

            ReceiptPrintForm = "InvoiceA4";

            CurrencyCode = RelayDataHelper.FindStringValueInJson(jsonInvoice, "Code", "Currency") ?? "ZWG";
        }


        private void DetermineInvoiceType(string jsonInvoice)
        {
            ReceiptType = ReceiptType.FiscalInvoice;

            if (Referrer.Contains("credit-note-view"))
            {
                ReceiptNotes = RelayDataHelper.GetStringCustomField2Value(jsonInvoice, ManagerCustomField.ReceiptNotesGuid) ?? string.Empty;

                DeviceIDRef = GetIntValue(jsonInvoice, ManagerCustomField.DeviceIDGuid, "RefInvoice");
                DeviceSNRef = RelayDataHelper.GetStringCustomField2Value(jsonInvoice, ManagerCustomField.DeviceSNGuid, "RefInvoice");
                FiscalDayNoRef = GetIntValue(jsonInvoice, ManagerCustomField.FiscalDayNoGuid, "RefInvoice");
                ReceiptCounterRef = GetIntValue(jsonInvoice, ManagerCustomField.ReceiptCounterGuid, "RefInvoice");
                ReceiptGlobalNoRef = GetIntValue(jsonInvoice, ManagerCustomField.ReceiptGlobalNoGuid, "RefInvoice");

                ReceiptRefNo = RelayDataHelper.FindStringValueInJson(jsonInvoice, "Reference", "RefInvoice");
                ReceiptRefDate = GetDateTimeValue(jsonInvoice, ManagerCustomField.ReceiptDateGuid, "RefInvoice") ?? DateTimeOffset.MinValue;

                var referenced = ParseInvoiceReference(RelayDataHelper.GetStringCustomField2Value(jsonInvoice, ManagerCustomField.ServerResponseGuid, "RefInvoice"));
                ReceiptID = referenced?.SubmitReceiptResponse?.ReceiptID ?? 0;

                ReceiptType = ReceiptType.CreditNote;

                var salesUnitPrice = RelayDataHelper.FindStringValueInJson(jsonInvoice, "UnitPrice");
                if (decimal.TryParse(salesUnitPrice, out var salesUnitPriceDecimal) && salesUnitPriceDecimal < 0)
                {
                    ReceiptType = ReceiptType.DebitNote;
                }
            }
        }

        private int GetIntValue(string json, string key, string rootKey = null)
        {
            return (int)RelayDataHelper.GetDecimalCustomField2Value(json, key, rootKey);
        }
        private decimal GetDoubleValue(string json, string key, string rootKey = null)
        {
            return (decimal)RelayDataHelper.GetDecimalCustomField2Value(json, key, rootKey);
        }

        private static InvoiceReference? ParseInvoiceReference(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonConvert.DeserializeObject<InvoiceReference>(json); }
            catch (JsonException) { return null; }
        }
        private DateTimeOffset? GetDateTimeValue(string json, string key, string rootKey = null)
        {
            return DateTimeOffset.TryParse(RelayDataHelper.GetStringCustomField2Value(json, key, rootKey), out DateTimeOffset value) ? value : null;
        }
    }
}
