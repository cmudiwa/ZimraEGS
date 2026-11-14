# Manager Io - Zimra FDMS Integration

## ZimraEGS Application Testing Guide


## Purpose
This guide aims to assist users in integrating and testing the ZimraEGS application, from device registration to invoice reporting.

---

## What ZimraEGS Does
1. **Fiscalises from the invoice screen**: a **ZIMRA** button on the invoice and credit note views opens a panel that checks the document and sends it to ZIMRA FDMS. The fiscal day is opened when needed, and the QR code and receipt numbers are stored back on the document.
2. **Keeps the device on the server**: the device certificate, private key, receipt counters and every submitted receipt are kept in a database on the ZimraEGS server. Manager only stores a link to the device.
3. **Closes the fiscal day for you**: open fiscal days are closed automatically every night (23:30 Zimbabwe time by default).

ZimraEGS is a Manager **extension**. Manager removed the Relay button in November 2025; the extension framework replaces it. (The old `/relay` address is still served for older Manager versions.)

---

## Hosting

ZimraEGS is an ASP.NET Core 8 application that you host yourself.

```bash
dotnet run --project ZimraEGS --urls http://localhost:5065
```

Settings (in `appsettings.json` or as environment variables):

| Setting | Meaning |
| --- | --- |
| `DataDirectory` | Folder for the database (`zimraegs.db`) and the encryption keys (`keys/`). It must survive restarts. Default: `$HOME/ZimraEGS-data`. |
| `AutoCloseDay:Enabled`, `AutoCloseDay:TimeOfDay` | Automatic fiscal day close and its time (Zimbabwe local time, `HH:mm`). |
| `Fdms:SkipServerCertificateValidation` | Leave `false`. Only for hosts that cannot validate the ZIMRA server certificate. |

**Back up the whole `DataDirectory`.** The database and the `keys` folder belong together: private keys in the database are encrypted with the keys in that folder. If the folder is lost, each device has to be registered with ZIMRA again.

Run one instance only; the receipt chain of a device is serialised inside the process.

---

## Steps to Use ZimraEGS

### 1. Adding a New Device
#### Access ZIMRA Fiscalisation Data Management System
1. Open this URL: [ZIMRA FDMS Test Environment](http://fdmsops.zimra.co.zw/fdms-public/add-device).
2. Complete all fields in the **Add New Device** form:
   - **TIN** and **Taxpayer** should follow the required format but do not need to be real.
   - Select the following options:
     - **Device Model Name**: *Server*
     - **Integrator**: *Self (server to server)*
3. Save all the information provided after filling out the form.

---

### 2. Adding the Extension to Manager
In your business in Manager, add an extension (shown as **Extensions** or **Custom Buttons** under Settings, depending on the version):

| Field | Value |
| --- | --- |
| Name | `ZIMRA` |
| Source | `Url` |
| Endpoint | `http://localhost:5065/extension` (your server's address followed by `/extension`) |
| Placement | `sales-invoice-view` |

Opening that address in a browser shows the same values for your server. If Manager is opened over `https`, ZimraEGS must be served over `https` too.

---

### 3. Connecting the Business to ZIMRA
1. Open any sales invoice and click the **ZIMRA** button. The panel shows the setup form.
2. Enter the **Device ID**, **Serial Number** and **Activation Key** from ZIMRA, and the **device model name** exactly as on the ZIMRA form. Click **Check device**.
3. Confirm the taxpayer ZIMRA shows and click **Yes, connect this business**.

That click registers the device with ZIMRA, creates the custom fields, the invoice theme and the tax codes in Manager, adds the ZIMRA button to credit notes, and links the business to the device. The tax codes are created from the taxes ZIMRA configured for the device, so they carry the right tax IDs and rates. On the test environment a few sample items and customers are added as well.

You can also choose a **default payment type** (for example Cash, or Credit for invoices not paid yet). It is used for every document that does not state how it was paid, and can be changed later in the panel.

---

### 4. Fiscalising Invoices
1. Set the base currency (Settings - Currencies - Base Currency) to `ZWG` or `USD`.
2. In the invoice form settings, tick **Custom Theme** and choose `ZimTheme`.
3. Create the invoice with the tax codes created by setup. VAT registered taxpayers need an **HS Code** on each line or on the inventory item.
4. Open the invoice and click **ZIMRA**. The panel shows the type, customer, tax and total. Click **Send to ZIMRA**. You return to the invoice with its QR code.

Payment is optional on the invoice:
- nothing entered: the device's default payment type is used for the full total;
- only a **Payment Type**: the full total is reported as paid that way;
- types and amounts (for split payments): the amounts must add up to the invoice total.

A customer needs both a Registered Name and a TIN, or neither. Tax codes you made yourself work too: they are matched to the ZIMRA taxes by their rate. Line discounts, as a percentage or an amount, are sent as discount lines.

If something is missing, the panel lists what to fix in plain words and nothing is sent.

Opening the panel on a document that is already fiscalised shows its receipt and never sends it twice.

#### When ZIMRA changes your taxes
ZIMRA can change the taxes allowed for a device, for example when a VAT rate changes. The panel then says which tax is no longer allowed. Open **Tax codes** in the panel and click **Update tax codes in Manager**, then use the updated codes.

#### Credit and debit notes
Create a credit note that refers to the fiscalised invoice, enter the reason in **Receipt Notes**, open it and click **ZIMRA**. A credit note entered with negative prices is sent as a debit note.

---

### 5. Fiscal Days
- A fiscal day is opened by the first document sent after the previous day was closed.
- Days are closed automatically every night, and in any case before ZIMRA's maximum day length.
- To close earlier, open the panel on any document and click **Close fiscal day now**.

---

## Key Points to Understand
1. **Invoice Date and Number** are set at fiscalisation: the date is the submission time, the number is `FiscalDay-ReceiptCounter`.
2. **Lost connection**: if ZIMRA does not answer, the signed receipt is kept and sent again, unchanged, the next time a document is sent. The chain cannot go out of step.
3. **Receipts ZIMRA flags** (red or grey validation errors) are recorded by ZIMRA and stay part of the fiscal day. Correct them with a credit note. Such a day may have to be closed in the ZIMRA portal.
4. **Upgrading from a version that kept the certificate in Manager**: just open the panel on an invoice. The device and its open fiscal day are imported, and the certificate and key are removed from Manager's business data.

---

## Development

```bash
dotnet test
```

The tests run the whole flow against a stand-in FDMS that re-checks signatures, numbering and day totals.

Thanks.
