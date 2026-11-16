import json, sys, re
from openpyxl import Workbook
from openpyxl.styles import Font, PatternFill, Alignment, Border, Side
from openpyxl.utils import get_column_letter
from openpyxl.worksheet.table import Table, TableStyleInfo

d = json.load(open(sys.argv[1])); out = sys.argv[2]
chapters = {int(k): v for k, v in d['chapters'].items()}

# Heading titles the printed handbook leaves out; texts from the WCO HS 2022 nomenclature.
MISSING = {
    '03.09': 'FLOURS, MEALS AND PELLETS OF FISH, CRUSTACEANS, MOLLUSCS AND OTHER AQUATIC INVERTEBRATES, FIT FOR HUMAN CONSUMPTION.',
    '04.10': 'INSECTS AND OTHER EDIBLE PRODUCTS OF ANIMAL ORIGIN, NOT ELSEWHERE SPECIFIED OR INCLUDED.',
    '08.03': 'BANANAS, INCLUDING PLANTAINS, FRESH OR DRIED.',
    '11.03': 'CEREAL GROATS, MEAL AND PELLETS.',
    '85.43': 'ELECTRICAL MACHINES AND APPARATUS, HAVING INDIVIDUAL FUNCTIONS, NOT SPECIFIED OR INCLUDED ELSEWHERE IN THIS CHAPTER.',
}

# Two entries are overlaid with a garbled copy in the PDF; their text as printed underneath.
OVERRIDE = {
    '0106.31.00': 'Birds of prey',
    '0210.92.00': 'Of whales, dolphins and porpoises (mammals of the order Cetacea); of manatees and dugongs (mammals of the order Sirenia); of seals, sea lions and walruses (mammals of the suborder Pinnipedia)',
}
for r in d['rows']:
    if r['code'] in OVERRIDE: r['own'] = OVERRIDE[r['code']]

rows, seen = [], set()
for r in d['rows']:
    if r['chapter'] == 98 or r['code'] in seen:     # Chapter 98 is flat rates of assessment, not a classification
        continue
    seen.add(r['code'])
    title = r['heading_title'] or MISSING.get(r['heading'], '')
    parts = [p for p in r['parents'] if p] + [r['own']]
    # A line that only says "Other" means little on its own: lead with the heading it belongs to.
    if all(p.lower().rstrip(':') == 'other' for p in parts) and title:
        parts = [title.rstrip('.').capitalize()] + parts
    desc = ' > '.join(parts)
    # The quantity unit ("Kg") sometimes lands inside a wrapped description; a real weight follows a number.
    desc = re.sub(r'(?<![\d.,])\s+Kg(?=\s)', '', desc)
    rows.append([r['code'].replace('.', ''), r['code'], desc, r['heading'].replace('.', ''), title,
                 f"{r['chapter']:02d}", chapters.get(r['chapter'], '')])
rows.sort(key=lambda x: x[0])

F = 'Arial'
head_font = Font(name=F, bold=True, color='FFFFFF'); head_fill = PatternFill('solid', fgColor='1F4E78')
body = Font(name=F, size=10); bold = Font(name=F, size=10, bold=True); title_font = Font(name=F, size=14, bold=True)
thin = Side(style='thin', color='BFBFBF')
wrap = Alignment(wrap_text=True, vertical='top'); top = Alignment(vertical='top')

wb = Workbook()

# ---- Read me
ws = wb.active; ws.title = 'Read me'
ws['A1'] = 'Zimbabwe HS codes for fiscalisation (FDMS)'; ws['A1'].font = title_font
lines = [
    ('What this is', 'Every 8-digit tariff code in ZIMRA\'s Customs and Excise Tariff (S.I. 203 of 2022), with its description, heading and chapter, plus ZIMRA\'s general codes for services. Use the code in column A of "HS Codes" as the HS Code of an item in Manager.'),
    ('Number of codes', "=COUNTA('HS Codes'!A:A)-1"),
    ('Services codes', "=COUNTA(Services!A:A)-1"),
    ('', ''),
    ('ZIMRA rules for fiscal devices', ''),
    ('Standard-rated (15%) lines', 'At least the first 4 digits (column D of "HS Codes"); the full 8 digits may be used.'),
    ('0% and exempt lines', 'The full 8 digits are required.'),
    ('Services', 'Use the general codes on the "Services" sheet, with a description of the service.'),
    ('Printing the code', 'Optional on the printed invoice, but the code must always be sent to ZIMRA.'),
    ('', ''),
    ('How to find a code', 'Filter column C (Description) on a key word, or column F (Chapter) on the chapter, then pick the most specific line. Lines are listed under their parents, joined by ">", e.g. "Portland cement > Other".'),
    ('Not sure of a code?', 'A clearing agent or ZIMRA can confirm it; ZIMRA also issues binding classification rulings (advance rulings).'),
    ('', ''),
    ('Sources', ''),
    ('Tariff', 'ZIMRA, Customs and Excise (Tariff) Notice, 2022, Statutory Instrument 203 of 2022 (Tariff Handbook), downloaded from https://www.zimra.co.zw/customs/classification-of-goods/tariff-handbook'),
    ('Rules and services codes', 'ZIMRA, "Use of Harmonized System codes (HS codes) under domestic taxes in recording of sales transactions using fiscal devices", https://www.zimra.co.zw/news/2308-use-of-harmonized-system-codes-hs-codes-under-domestic-taxes-in-recording-of-sales-transactions-using-fiscal-devices'),
    ('', ''),
    ('Notes', ''),
    ('Later amendments', 'Tariff amendments are published as statutory instruments (for example S.I. 139 of 2024). A code changed after 2022 may differ; confirm doubtful codes on ZIMRA\'s E-Tariff tool, https://etariff.zimra.co.zw/'),
    ('Left out', 'Chapter 98 (flat rates of assessment for travellers\' goods) and the Second Schedule (excise duties): they are not product classifications. Codes the handbook prints more than once are listed once.'),
    ('Duty rates', 'Not included: they are customs duties on imports, not VAT, and do not decide the VAT treatment of a sale.'),
    ('Filled-in headings', 'The printed handbook omits the titles of headings 03.09, 04.10, 08.03, 11.03 and 85.43. They are taken from the WCO Harmonized System 2022 nomenclature.'),
    ('Extraction', 'Codes and descriptions were extracted from the PDF by program and checked for gaps and duplicates. Descriptions that wrap over several lines in the handbook are joined. Spelling is as printed in the handbook.'),
]
for i, (a, b) in enumerate(lines, start=3):
    ws.cell(i, 1, a).font = bold if (a and not b) else body
    c = ws.cell(i, 2, b); c.font = body; c.alignment = wrap
    ws.cell(i, 1).alignment = top
    if a and not b:
        ws.cell(i, 1).font = Font(name=F, size=11, bold=True)
for r in (4, 5):
    ws.cell(r, 2).number_format = '#,##0'; ws.cell(r, 2).alignment = Alignment(horizontal='left', vertical='top')
ws.column_dimensions['A'].width = 30; ws.column_dimensions['B'].width = 110

# ---- HS Codes
ws = wb.create_sheet('HS Codes')
headers = ['HS code (enter in Manager)', 'Tariff code', 'Description', '4-digit code', 'Heading description', 'Chapter', 'Chapter description']
ws.append(headers)
for row in rows:
    ws.append(row)
widths = [16, 13, 70, 11, 55, 9, 55]
for i, w in enumerate(widths, 1):
    ws.column_dimensions[get_column_letter(i)].width = w
for c in ws[1]:
    c.font = head_font; c.fill = head_fill; c.alignment = Alignment(wrap_text=True, vertical='center')
ws.row_dimensions[1].height = 30
for row in ws.iter_rows(min_row=2):
    for c in row:
        c.font = body; c.alignment = wrap
    for i in (0, 1, 3, 5):
        row[i].number_format = '@'          # codes are text: keep leading zeros
tab = Table(displayName='HSCodes', ref=f"A1:G{len(rows) + 1}")
tab.tableStyleInfo = TableStyleInfo(name='TableStyleLight9', showRowStripes=True)
ws.add_table(tab)
ws.freeze_panes = 'B2'

# ---- Services
ws = wb.create_sheet('Services')
ws.append(['HS code (enter in Manager)', 'Description', 'VAT'])
for row in [['99001000', 'Provision of services at 15% VAT (describe the service, e.g. accommodation)', 'Standard rate'],
            ['99002000', 'Provision of services at 0% VAT (describe the service)', 'Zero rate'],
            ['99003000', 'Provision of services exempt from VAT (describe the service)', 'Exempt']]:
    ws.append(row)
for c in ws[1]:
    c.font = head_font; c.fill = head_fill
for row in ws.iter_rows(min_row=2):
    for c in row: c.font = body
    row[0].number_format = '@'
ws.column_dimensions['A'].width = 26; ws.column_dimensions['B'].width = 80; ws.column_dimensions['C'].width = 16
ws['B6'] = 'Source: ZIMRA notice on HS codes under domestic taxes (see "Read me"). These codes are not in the customs tariff.'
ws['B6'].font = Font(name=F, size=9, italic=True)

# Printing: landscape, one page wide, header row repeated on every page.
for sheet in wb.worksheets:
    sheet.page_setup.orientation = 'landscape'
    sheet.page_setup.paperSize = sheet.PAPERSIZE_A4
    sheet.page_setup.fitToWidth = 1
    sheet.page_setup.fitToHeight = 0
    sheet.sheet_properties.pageSetUpPr.fitToPage = True
    sheet.print_options.gridLines = False
wb['HS Codes'].print_title_rows = '1:1'

wb.active = 1
wb.save(out)
print(len(rows), 'codes written')
