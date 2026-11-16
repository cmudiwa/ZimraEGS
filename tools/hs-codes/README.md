# HS code list

`ZimraEGS/Resources/hs-codes.json` holds every 8-digit code of ZIMRA's Customs and Excise Tariff
(Statutory Instrument 203 of 2022, the Tariff Handbook) plus ZIMRA's three general codes for services.
The ZIMRA panel uses it to find and check HS codes for items.

To rebuild it from a new edition of the handbook:

```bash
pdftotext -layout handbook.pdf layout.txt
python3 parse_tariff.py layout.txt parsed.json
python3 build_catalog.py parsed.json ../../ZimraEGS/Resources/hs-codes.json
python3 build_workbook.py parsed.json Zimbabwe_HS_Codes.xlsx   # optional Excel list
```

The handbook is downloaded from https://www.zimra.co.zw/customs/classification-of-goods/tariff-handbook.
`parse_tariff.py` has page numbers specific to the 2022 edition (the excise schedule starts at page 1130);
check them against a new edition.
