using Newtonsoft.Json;
using ZimraEGS.ApiClient.Enums;
using ZimraEGS.ApiClient.Models;
using ZimraEGS.Data;
using ZimraEGS.Helpers;
using ZimraEGS.Models;

namespace ZimraEGS.Services
{
    /// <summary>One PUT the browser makes against the Manager API to store fiscalisation results.</summary>
    public class ManagerApiCall
    {
        public string ApiUrl { get; set; } = string.Empty;
        public string Payload { get; set; } = string.Empty;
    }

    /// <summary>
    /// Builds the updates written back into Manager. Manager often runs on the user's own machine,
    /// so the browser performs these calls; they only carry display data and can be repeated at any time.
    /// </summary>
    public static class ManagerWriteBack
    {
        public const string BusinessDetailsFormKey = "38cf4712-6e95-4ce1-b53a-bff03edad273";

        public static ManagerApiCall Invoice(RelayData relay, Device device, FiscalReceipt row)
        {
            var receiptDate = TimeZoneHelper.ParseFdmsString(row.ReceiptDate) ?? TimeZoneHelper.GetSouthAfricaTime();

            SubmitReceiptResponse? submitResponse = null;
            if (!string.IsNullOrEmpty(row.ResponseJson))
            {
                try { submitResponse = JsonConvert.DeserializeObject<SubmitReceiptResponse>(row.ResponseJson); }
                catch (JsonException) { }
            }

            var reference = new InvoiceReference
            {
                ApprovalStatus = row.Status,
                ReceiptHash = row.Hash,
                ReceiptSignature = row.Signature,
                SubmitReceiptResponse = submitResponse
            };

            string json = relay.InvoiceJson;
            json = RelayDataHelper.ModifyStringCustomFields2(json, ManagerCustomField.ApprovalStatusGuid, row.Status);
            json = RelayDataHelper.UpdateOrCreateField(json, row.ReceiptType == ReceiptType.FiscalInvoice ? "IssueDate" : "Date", receiptDate.ToString("yyyy-MM-dd"));
            json = RelayDataHelper.ModifyStringCustomFields2(json, ManagerCustomField.ReceiptDateGuid, receiptDate.ToString("yyyy-MM-dd HH:mm:ss"));
            json = RelayDataHelper.UpdateOrCreateField(json, "Reference", row.InvoiceNo);
            json = RelayDataHelper.ModifyStringCustomFields2(json, ManagerCustomField.ServerResponseGuid, JsonConvert.SerializeObject(reference, Formatting.Indented));
            json = RelayDataHelper.ModifyStringCustomFields2(json, ManagerCustomField.ReceiptQRCodeGuid, row.QrCode);
            json = RelayDataHelper.ModifyStringCustomFields2(json, ManagerCustomField.VerificationCodeGuid, row.VerificationCode);
            json = RelayDataHelper.ModifyStringCustomFields2(json, ManagerCustomField.DeviceSNGuid, device.SerialNumber);
            json = RelayDataHelper.ModifyDecimalCustomFields2(json, ManagerCustomField.DeviceIDGuid, device.DeviceID);
            json = RelayDataHelper.ModifyDecimalCustomFields2(json, ManagerCustomField.FiscalDayNoGuid, row.FiscalDayNo);
            json = RelayDataHelper.ModifyDecimalCustomFields2(json, ManagerCustomField.ReceiptGlobalNoGuid, row.ReceiptGlobalNo);
            json = RelayDataHelper.ModifyDecimalCustomFields2(json, ManagerCustomField.ReceiptCounterGuid, row.ReceiptCounter);

            if (row.ReceiptType != ReceiptType.FiscalInvoice && !string.IsNullOrEmpty(row.RefJson))
            {
                var r = JsonConvert.DeserializeObject<ReceiptReferenceInfo>(row.RefJson)!;
                json = RelayDataHelper.ModifyDecimalCustomFields2(json, ManagerCustomField.DeviceIDRefGuid, r.DeviceIDRef);
                json = RelayDataHelper.ModifyStringCustomFields2(json, ManagerCustomField.DeviceSNRefGuid, r.DeviceSNRef);
                json = RelayDataHelper.ModifyDecimalCustomFields2(json, ManagerCustomField.FiscalDayNoRefGuid, r.FiscalDayNoRef);
                json = RelayDataHelper.ModifyDecimalCustomFields2(json, ManagerCustomField.ReceiptGlobalNoRefGuid, r.ReceiptGlobalNoRef);
                json = RelayDataHelper.ModifyDecimalCustomFields2(json, ManagerCustomField.ReceiptCounterRefGuid, r.ReceiptCounterRef);
                json = RelayDataHelper.ModifyStringCustomFields2(json, ManagerCustomField.ReceiptNoRefGuid, r.ReceiptRefNo);
                json = RelayDataHelper.ModifyStringCustomFields2(json, ManagerCustomField.ReceiptRefDateGuid, r.ReceiptRefDate);
            }

            return new ManagerApiCall { ApiUrl = Utils.ConstructInvoiceApiUrl(relay.Referrer, relay.FormKey), Payload = json };
        }

        /// <summary>
        /// Replaces the old certificate blob and chain state in the business details with the device link.
        /// After this the private key no longer exists anywhere in Manager.
        /// </summary>
        public static ManagerApiCall BusinessLink(string api, string businessDetailsJson, string deviceLink)
        {
            string json = string.IsNullOrWhiteSpace(businessDetailsJson) ? "{}" : businessDetailsJson;
            json = RelayDataHelper.ModifyStringCustomFields2(json, ManagerCustomField.CertificateInfoGuid, deviceLink);
            json = RelayDataHelper.ModifyStringCustomFields2(json, ManagerCustomField.BusinessReferenceGuid, null);
            json = RelayDataHelper.ModifyStringCustomFields2(json, ManagerCustomField.FiscalDaySummaryGuid, null);
            json = RelayDataHelper.ModifyStringCustomFields2(json, ManagerCustomField.AppVersionGuid, VersionHelper.GetVersion());

            return new ManagerApiCall { ApiUrl = $"{api.TrimEnd('/')}/business-details-form/{BusinessDetailsFormKey}", Payload = json };
        }
    }
}
