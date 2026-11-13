# Manager Io - Zimra FDMS Integration

## ZimraEGS Application Testing Guide


## Purpose
This guide aims to assist users in integrating and testing the ZimraEGS application, from device registration to invoice reporting.

---

## What ZimraEGS Does
1. **One-click fiscalisation**: click **Relay** on an invoice in Manager. ZimraEGS opens the fiscal day if needed, sends the receipt to ZIMRA FDMS, stores the QR code and receipt numbers back on the invoice and returns you to Manager.
2. **Keeps the device on the server**: the device certificate, private key, receipt counters and every submitted receipt are kept in a database on the ZimraEGS server. Manager only stores a link to the device.
3. **Closes the fiscal day for you**: open fiscal days are closed automatically every night (23:30 Zimbabwe time by default).

---

## Hosting

ZimraEGS is an ASP.NET Core 8 application that you host yourself.

```bash
dotnet run --project ZimraEGS
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

### 2. Connecting a Manager Business
1. In **Manager.IO**, enable **Sales Invoices** and create an invoice.
2. In the invoice form settings, tick **Relay** and enter your server's relay URL, for example `https://your-server/relay`.
3. Open the invoice and click **Relay**. A business without a device is taken to the setup page.
4. Enter the **Device ID**, **Serial Number** and **Activation Key** from ZIMRA and click **Check device**.
5. Confirm the taxpayer ZIMRA shows and click **Yes, connect this business**.

That one click registers the device with ZIMRA, creates the custom fields, the invoice theme and the tax codes in Manager, and links the business to the device. The tax codes are created from the taxes ZIMRA configured for the device, so they carry the right tax IDs. On the test environment a few sample items and customers are added as well.

On the setup page you can also choose a **default payment type** (for example Cash, or Credit for invoices not paid yet). It is used for every invoice that does not state how it was paid, and can be changed later from the receipt page.

---

### 3. Fiscalising Invoices
1. Set the base currency (Settings - Currencies - Base Currency) to `ZWG` or `USD`.
2. In the invoice form settings, tick **Custom Theme** and choose `ZimTheme`.
3. Create the invoice with the tax codes created by setup. VAT registered taxpayers need an **HS Code** on each line or on the inventory item.
4. Click **Relay**. That is all: the fiscal day is opened automatically, and you return to the invoice with its QR code.

Payment is optional on the invoice:
- nothing entered: the device's default payment type is used for the full total;
- only a **Payment Type**: the full total is reported as paid that way;
- types and amounts (for split payments): the amounts must add up to the invoice total.

A customer needs both a Registered Name and a TIN, or neither. Tax codes you made yourself work too: they are matched to the ZIMRA taxes by their rate.

If something is missing, a page lists what to fix in plain words and nothing is sent.

Clicking **Relay** on an invoice that is already fiscalised shows its receipt and never sends it twice.

#### Credit and debit notes
Create a credit note that refers to the fiscalised invoice, enter the reason in **Receipt Notes**, and click **Relay**. A credit note entered with negative prices is sent as a debit note.

---

### 4. Fiscal Days
- A fiscal day is opened by the first invoice relayed after the previous day was closed.
- Days are closed automatically every night, and in any case before ZIMRA's maximum day length.
- To close earlier, relay any fiscalised invoice and click **Close fiscal day**.

---

## Key Points to Understand
1. **Invoice Date and Number** are set at fiscalisation: the date is the submission time, the number is `FiscalDay-ReceiptCounter`.
2. **Lost connection**: if ZIMRA does not answer, the signed receipt is kept and sent again, unchanged, on the next relay. The chain cannot go out of step.
3. **Receipts ZIMRA flags** (red or grey validation errors) are recorded by ZIMRA and stay part of the fiscal day. Correct them with a credit note. Such a day may have to be closed in the ZIMRA portal.
4. **Upgrading from a version that kept the certificate in Manager**: just relay an invoice. The device and its open fiscal day are imported, and the certificate and key are removed from Manager's business data.

---

## Development

```bash
dotnet test
```

The tests run the whole flow against a stand-in FDMS that re-checks signatures, numbering and day totals.

Thanks.
