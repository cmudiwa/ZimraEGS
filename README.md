# Fiscal Managerio - ZIMRA FDMS fiscalisation for Manager.io

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
2. Create the invoice with the tax codes created by setup. VAT registered taxpayers need an **HS Code** on each line or on the inventory item.
3. Open the invoice and click **ZIMRA**. The panel shows the type, customer, tax and total. Click **Send to ZIMRA**. You return to the invoice, which now prints in ZIMRA's layout with its QR code.

Fiscalised documents are switched to the `ZimTheme` theme automatically. It follows the A4 invoice layout of ZIMRA's API specification: verification code and QR code, the title ZIMRA prescribes (FISCAL TAX INVOICE, FISCAL INVOICE, CREDIT NOTE or DEBIT NOTE), seller and buyer, receipt numbers, fiscal day and device, and for a credit or debit note the details of the original invoice. A document that has not been sent is marked NOT FISCALISED.

Payment is optional on the invoice:
- nothing entered: the device's default payment type is used for the full total;
- only a **Payment Type**: the full total is reported as paid that way;
- types and amounts (for split payments): the amounts must add up to the invoice total.

A customer needs both a Registered Name and a TIN, or neither. Tax codes you made yourself work too: they are matched to the ZIMRA taxes by their rate. Line discounts, as a percentage or an amount, are sent as discount lines.

If something is missing, the panel lists what to fix in plain words and nothing is sent.

Opening the panel on a document that is already fiscalised shows its receipt and never sends it twice.

#### HS codes for items
ZIMRA needs an HS code on every invoice line of a VAT registered business: at least 4 digits on standard-rated lines, all 8 on zero-rated and exempt lines. The panel has an **HS codes for items** tool (open it from the panel, or from the "Fix HS codes on items" button when an invoice is refused for a missing code). It lists your inventory and non-inventory items with their HS codes, flags missing and invalid ones, suggests codes from each item's name, lets you search ZIMRA's tariff and apply a code to many items at once, and saves the codes on the items in Manager. No items are created.

The list comes from ZIMRA's Tariff Handbook (S.I. 203 of 2022) plus ZIMRA's general codes for services (99001000, 99002000, 99003000); see `tools/hs-codes`.

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

## Run with Docker

Two containers: the app, and Caddy in front of it for https (it gets and renews the certificate itself). The database, encryption keys, logs and backups live in the `zimraegs-data` volume. Run **one** copy only.

1. On a server with Docker, open ports 80 and 443 and point your domain at it. Then:
   ```bash
   git clone -b claude/peaceful-bell-yss965 https://github.com/cmudiwa/ZimraEGS.git /opt/fiscal && cd /opt/fiscal
   cp .env.example .env                                     # set DOMAIN=fiscal.yourdomain.co.zw
   cp appsettings.Local.example.json appsettings.Local.json # your admin e-mail and privacy details
   docker compose up -d --build
   ```
2. Open `https://<your domain>`, create your account with the admin e-mail, and in Manager set the extension's Endpoint to `https://<your domain>/extension`.

| Task | Command |
| --- | --- |
| Logs | `docker compose logs -f app` |
| Update | `git pull && docker compose up -d --build` |
| Back up now | `./deploy/backup.sh` (database + keys, safe while running; saved in `./backups`, newest 30 kept) |
| Back up daily | `crontab -e`, then `15 2 * * * /opt/fiscal/deploy/backup.sh >> /opt/fiscal/backups/backup.log 2>&1` |
| Restore | `./deploy/restore.sh backups/zimraegs-backup-<date>.tar.gz` (asks you to type RESTORE) |
| Reset a password | `docker compose exec app dotnet ZimraEGS.dll reset-password you@example.com` |

Backups hold the keys that decrypt your devices' private keys: copy `./backups` off the server (another machine or encrypted storage) and keep it private. **Never** run `docker compose down -v`: `-v` deletes the data volume, and with it every device's registration.

Moving from your PC: stop the app on the PC, run `dotnet run --project ZimraEGS -- backup` there (it prints where the backup file went), copy that file to the server's `backups` folder, and run `./deploy/restore.sh` with it. Without Docker, `dotnet ZimraEGS.dll backup` works the same and writes to `<data folder>/backups`.

## Self-hosting on a Linux server (for example Hetzner)

The app is one ASP.NET Core 8 program with its own SQLite database: no separate database server is needed. Run **one** copy; it closes fiscal days on a timer.

1. **Server.** Ubuntu 24.04, 2 GB RAM is enough. Open ports 22, 80 and 443 only. Point a domain (for example `fiscal.example.com`) at the server's IP address.
2. **Install .NET and Caddy** (Caddy gets and renews the https certificate by itself):
   ```bash
   sudo apt update && sudo apt install -y dotnet-sdk-8.0 caddy git sqlite3
   ```
3. **User, folders and the app:**
   ```bash
   sudo useradd --system --create-home --home-dir /var/lib/zimraegs zimraegs
   sudo git clone -b claude/peaceful-bell-yss965 https://github.com/cmudiwa/ZimraEGS.git /opt/zimraegs-src
   sudo dotnet publish /opt/zimraegs-src/ZimraEGS -c Release -o /opt/zimraegs
   ```
4. **Your settings** in `/opt/zimraegs/appsettings.Local.json` (admin e-mail, `Privacy` details, and `"Security": { "HttpsOnly": true }`).
5. **Service** `/etc/systemd/system/zimraegs.service`:
   ```ini
   [Unit]
   Description=Fiscal Managerio
   After=network-online.target

   [Service]
   User=zimraegs
   WorkingDirectory=/opt/zimraegs
   ExecStart=/usr/bin/dotnet /opt/zimraegs/ZimraEGS.dll --urls http://127.0.0.1:5065
   Environment=DataDirectory=/var/lib/zimraegs/data
   Environment=ASPNETCORE_ENVIRONMENT=Production
   Restart=always
   RestartSec=5

   [Install]
   WantedBy=multi-user.target
   ```
   ```bash
   sudo systemctl daemon-reload && sudo systemctl enable --now zimraegs
   ```
6. **https** in `/etc/caddy/Caddyfile`, then `sudo systemctl reload caddy`:
   ```
   fiscal.example.com {
       reverse_proxy 127.0.0.1:5065
   }
   ```
   The app trusts the `X-Forwarded-Proto` and `X-Forwarded-For` headers of a proxy on the same machine.
7. **Backups.** `/var/lib/zimraegs/data` holds the database and the key ring that decrypts the device keys: back up both, together, every day, and keep a copy off the server. `sqlite3 zimraegs.db ".backup copy.db"` copies the database safely while the app runs.
8. **Updates:** `git -C /opt/zimraegs-src pull`, publish again as in step 3, then `sudo systemctl restart zimraegs`. `appsettings.Local.json` and the data folder are not touched.

Logs: `journalctl -u zimraegs -f`, and daily files under `/var/lib/zimraegs/LogFiles`.

## Invoices from Excel

Businesses that do not use Manager can fiscalise invoices kept in Excel, at `/excel` (sign-in needed):

1. **Connect a device** with the Device ID, Serial Number and Activation Key from ZIMRA. It is registered from the server and linked to the account. A device already set up in the Manager panel can be connected with the same details: it is not registered again, and Manager and Excel share it.
2. **Download the template** for the device. It has one row per invoice line, drop-down lists (including the taxes ZIMRA set for the device), and help on each column heading.
3. **Upload** the filled-in workbook. Every document is checked first, with problems reported by row; nothing is sent until you press Send.
4. **Send.** Documents go through the same checks, numbering, signing and fiscal day handling as Manager's. Invoices are sent before credit and debit notes, so a note can refer to an invoice in the same workbook. A document number already sent is never sent again.
5. **Results.** Download the workbook with the fiscal number, status, verification code and QR link filled in, and print each document (or all) in ZIMRA's layout, with the same theme Manager uses. The printout is the fiscal document.

Credit and debit notes give the original invoice (its fiscal number, or the customer's own number if it was sent from Excel) and a reason; prices stay positive. Uploaded workbooks are deleted after `Privacy:ExcelRetentionDays` (30); the fiscal documents are kept. At most 300 documents, 3,000 rows and 5 MB per upload.

## Subscriptions (live devices)

Test devices are free. A live (production) device sends documents while its subscription is paid up:

- **Accounts.** Customers create an account at `/account/signup`. When a live device is connected in the ZIMRA panel, the panel asks for the account's e-mail and password and links the device to it. A new live device gets a trial (`Billing:TrialDays`, 14 by default).
- **Payments are recorded by hand.** There is no payment gateway. When a customer pays, an administrator opens `/admin`, opens the device and records the payment: months or days paid, amount, and how it was paid with its reference. The paid period is extended from its current end, or from today if it has already ended.
- **What stops, and what never does.** When a subscription has ended and the grace period (`Billing:GraceDays`, 7) is over, new documents are not sent to ZIMRA and the panel says so with a link to the account. A receipt whose answer from ZIMRA was lost is still sent, closing the fiscal day (by hand or automatically) still works, and fiscalised documents can still be opened and printed. A lapsed payment never breaks the fiscal chain or leaves a day open.
- **Warnings.** The panel warns from `Billing:WarnDays` (7) days before the paid period ends.
- **Customers** see their devices, paid-until dates and payments at `/account`, and can change their password. A customer who forgot their password gets a new one from an administrator (`/admin`, open the customer, "Set a new password").
- **Administrator's own password.** On the server, stop the program and run `dotnet run --project ZimraEGS -- reset-password you@example.com`. It prints a new password for that account; sign in with it and change it.

Put your own settings in `ZimraEGS/appsettings.Local.json` (create it next to `appsettings.json`). It overrides `appsettings.json` and is not part of the repository, so `git pull` never conflicts with it. For example:

```json
{
  "Billing": { "AdminEmails": ["you@example.com"], "Price": "USD 10 a month per device" },
  "Privacy": { "ControllerName": "Your Business (Pvt) Ltd", "ContactEmail": "privacy@example.com" }
}
```

Environment variables work too (for example `Billing__AdminEmails__0=you@example.com`). The settings:

| Setting | Meaning |
| --- | --- |
| `Billing:Enabled` | `false` turns subscriptions off: every device may send. |
| `Billing:AdminEmails` | E-mail addresses whose accounts may open `/admin`. Create the account at `/account/signup` with one of these addresses. |
| `Billing:TrialDays`, `Billing:GraceDays`, `Billing:WarnDays` | Trial for a new live device, days of sending after the paid period, days of warning before it ends. |
| `Billing:Price`, `Billing:PaymentInstructions` | Shown to customers: the price and how to pay you. |

When the server is reachable from the internet, serve it over https only, so passwords and sign-in cookies are never sent in the clear.

## Data protection (POTRAZ)

Customers' details, taxpayer details and the buyer details on fiscal documents are personal data under the Cyber and Data Protection Act [Chapter 12:07] and its regulations (SI 155 of 2024), administered by POTRAZ. The service supports what the law asks of you:

- **Privacy notice** at `/privacy`, linked from every page, sign-up and the panel. It is filled from the `Privacy` settings below.
- **Consent.** Sign-up needs the customer to agree to the notice; the version and time are recorded. When `Privacy:NoticeVersion` changes, customers are asked to agree again on their account page.
- **Customers' rights.** On `/account` a customer corrects their details, downloads everything held about them (JSON), and deletes their account. Administrators do the same for requests by e-mail or letter (`/admin`, open the customer).
- **Deletion and retention.** Deleting an account removes it and the customer's e-mail from the security log. Devices, fiscal documents and payments are kept, unlinked, because tax law requires them (`Privacy:RecordsRetentionYears`). The security log and server log files are deleted after `Privacy:AuditRetentionDays` and `Privacy:LogRetentionDays`.
- **Security log** at `/admin/privacy`: sign-ins, refused sign-ins, account changes, data downloads and every administrator action on a customer, with time and IP address.
- **Security.** Passwords are salted hashes with lock-out; device keys are encrypted at rest; account and admin pages are not cached and cannot be framed. Set `Security:HttpsOnly` to `true` once the server is served over https: http is then redirected, HSTS is sent and the sign-in cookie is https-only.

What only you can do, listed with reminders on `/admin/privacy`: apply to POTRAZ for a data controller licence (Form DP1) and renew it yearly, appoint a data protection officer and notify POTRAZ (Form DP2), report a breach to POTRAZ within 24 hours (Form DP3), and keep the server and backups in Zimbabwe or check with POTRAZ before hosting abroad. This is a guide, not legal advice.

| Setting | Meaning |
| --- | --- |
| `Privacy:ControllerName`, `ControllerAddress`, `ContactEmail`, `ContactPhone` | Your business, shown as the data controller. |
| `Privacy:DpoName`, `Privacy:DpoEmail` | Your data protection officer. |
| `Privacy:PotrazLicenceNumber` | Your POTRAZ data controller licence. |
| `Privacy:HostingLocation` | Where the server and its data are, e.g. `Harare, Zimbabwe`. |
| `Privacy:NoticeVersion` | Change it when the notice changes in substance. |
| `Privacy:RecordsRetentionYears`, `AuditRetentionDays`, `LogRetentionDays`, `ExcelRetentionDays` | How long records, the security log, log files and uploaded Excel workbooks are kept. |
| `Security:HttpsOnly` | `true` once the server is served over https. |

## Key Points to Understand
1. **Invoice Date and Number** are set at fiscalisation: the date is the submission time, the number is `DeviceID-FiscalDay-ReceiptCounter`, which is unique for the taxpayer across all of its devices. Times are taken from ZIMRA's clock, so a slow or wrong clock on the server cannot misdate a receipt.
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
