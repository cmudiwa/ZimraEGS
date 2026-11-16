"""Builds ZimraEGS/Resources/hs-codes.json from the parsed tariff (see parse_tariff.py)."""
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


SERVICES = [
    ['99001000', 'Provision of services at 15% VAT (describe the service, e.g. accommodation)', '9900', 'SERVICES (FDMS GENERAL CLASSIFICATION)', '99', 'Services'],
    ['99002000', 'Provision of services at 0% VAT (describe the service)', '9900', 'SERVICES (FDMS GENERAL CLASSIFICATION)', '99', 'Services'],
    ['99003000', 'Provision of services exempt from VAT (describe the service)', '9900', 'SERVICES (FDMS GENERAL CLASSIFICATION)', '99', 'Services'],
]
codes = [[r[0], r[2]] for r in rows] + [[s[0], s[1]] for s in SERVICES]
headings = {r[3]: r[4] for r in rows}
headings['9900'] = SERVICES[0][3]
chapter_titles = {r[5]: r[6] for r in rows}
chapter_titles['99'] = 'Services'
json.dump({'source': 'ZIMRA Customs and Excise (Tariff) Notice, 2022 (S.I. 203 of 2022); services codes from ZIMRA notice on HS codes under domestic taxes',
           'codes': codes, 'headings': headings, 'chapters': chapter_titles},
          open(out, 'w', encoding='utf-8'), ensure_ascii=False, separators=(',', ':'))
print(len(codes), 'codes,', len(headings), 'headings,', len(chapter_titles), 'chapters')
