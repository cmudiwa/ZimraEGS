"""Parse ZIMRA's Customs and Excise Tariff (S.I. 203 of 2022) from pdftotext -layout output into rows."""
import re, json, sys

CODE = re.compile(r'^(\d{4}\.\d{2}\.?\d{2})(?!\d)(.*)$')   # a few entries are printed as 5603.1200
HEADING = re.compile(r'^(\d{2}\.\d{2})$')
WORD = re.compile(r'\S+')

UNIT_OR_RATE = re.compile(r'^(\$\S*|\S+/(Kg|kg|L|l|u|pr|t|m2|LAA|LA)|\.Kg|\d\.(Kg|kg|u|2u|L|l|t|m|m2|m3|LAA|pr|g|ct)|\d+(\.\d+)?%|US\$\S*|\+|Excise|Free|free|or|per|\d\.|Kg|kg|u|\du|L|l|t|m|m2|m3|LAA|LA|A|pr|g|carat|ct|kWh|GT|1000u|1000|kg/u|\d+(\.\d+)?|-)$')

def is_unit_or_rate(w):
    return bool(UNIT_OR_RATE.match(w))

def words(line):
    return [(m.start(), m.group()) for m in WORD.finditer(line)]

def split_dashes(text):
    """'- - - Other' -> (3, 'Other')"""
    m = re.match(r'^((?:-\s*)+)(.*)$', text.strip())
    if not m:
        return 0, text.strip()
    return m.group(1).count('-'), m.group(2).strip()

def clean(s):
    s = re.sub(r'\s+', ' ', s).strip()
    s = re.sub(r'\s*:\s*$', '', s)
    return s

def desc_done(desc, x, cols):
    # Text far right in the rate columns (e.g. 'Excise' notes) is not description.
    return x >= cols[1]

def parse(path):
    text = open(path, encoding='utf-8', errors='replace').read()
    pages = text.split('\f')
    rows, chapters = [], {}
    titles_by_row, heading_titles = [], {}
    cols = None                       # (quantity x, general x, mfn x)
    chapter = None
    heading, heading_title = None, ''
    stack = {}                        # dash level -> label
    current = None                    # what a following line continues: ('code', row) | ('group', level) | ('heading',)
    pending_chapter = None
    title_open = None
    for pno, page in enumerate(pages, 1):
        lines = page.split('\n')
        # column positions from this page's header, if it has one
        for i, l in enumerate(lines):
            if 'Quantity' in l and i + 1 < len(lines) and 'General' in lines[i + 1]:
                g = lines[i + 1]
                mfn = re.search(r'M\.?\s?F\.?\s?N|MFN', g)
                gx = g.index('General') - 3
                cols = (l.index('Quantity') - 2, gx, (mfn.start() - 3) if mfn else gx + 12)
                break
        blank = True
        for line in lines:
            s = line.strip()
            if not s:
                blank = True
                title_open = None
                continue
            if title_open is not None and not s.lower().startswith('notes'):
                chapters[title_open] = chapters[title_open] + ' ' + s   # a chapter title wrapped onto the next line
                continue
            if re.match(r'^(Heading|No\.)\s', s) or re.match(r'^Page \d+$', s) or s == 'RATE OF DUTY':
                continue
            m = re.match(r'^chapter\s+(\d+)\s*$', s, re.I)
            if m and line[:7].lower() == 'chapter':
                chapter = int(m.group(1)); pending_chapter = chapter; current = None; blank = False
                continue
            if pending_chapter is not None:
                chapters[pending_chapter] = s; title_open = pending_chapter; pending_chapter = None; blank = False
                continue

            line = re.sub(r'^(\d{2}) \.(\d{2})\b', r'\1.\2', line)   # '72 .17' in the print
            ws = words(line)
            first_x, first = ws[0]
            cm = CODE.match(first)
            # Entries start in the code column; a code further right is a reference inside a description.
            if cm and first_x < 16:
                printed = cm.group(1)
                code = printed if printed.count('.') == 2 else printed[:7] + '.' + printed[7:]
                if printed.count('.') == 1 and any(r['code'] == code for r in rows[-3:]):
                    # A second, garbled copy of the entry just above, overlaid in the PDF: ignore it.
                    current = ('code', next(r for r in reversed(rows) if r['code'] == code)); blank = False
                    continue
                cm_rest = cm.group(2)
                rest = [(first_x + len(printed), cm_rest)] if cm_rest else []
                rest += ws[1:]
                desc, unit, gen, mfn = [], [], [], []
                for x, w in rest:
                    if cols and x >= cols[0] and not is_unit_or_rate(w) and not desc_done(desc, x, cols):
                        desc.append(w)
                    elif cols and x >= cols[2]: mfn.append(w)
                    elif cols and x >= cols[1]: gen.append(w)
                    elif cols and x >= cols[0]: unit.append(w)
                    else: desc.append(w)
                level, own = split_dashes(' '.join(desc))
                if not own and not blank and current and current[0] == 'group':
                    # The description was printed on the line above the code.
                    level = current[1]; own = stack.get(level, '')
                for k in [k for k in stack if k >= level]: del stack[k]
                row = dict(code=code, chapter=int(code[:2]), heading=code[:2] + '.' + code[2:4], heading_title=heading_title,
                           level=level, own=own, parents=[stack[k] for k in sorted(stack)], unit=' '.join(unit),
                           general=' '.join(gen), mfn=' '.join(mfn), page=pno,
                           desc_x=(rest[0][0] if rest else first_x + 12))
                if row['heading'] != heading:      # a heading line was missed: keep the context consistent
                    heading, heading_title, stack = row['heading'], '', {}
                    row['heading_title'], row['parents'] = '', []
                rows.append(row); current = ('code', row); blank = False
                titles_by_row.append(heading)
                continue
            hm = HEADING.match(first)
            if hm and first_x < 10:
                heading = hm.group(1)
                heading_title = ' '.join(w for x, w in ws[1:] if (not cols or x < cols[0] or not is_unit_or_rate(w)) and not CODE.match(w))
                stack = {}; current = ('heading',); blank = False
                continue


            # text in the description column: a new group label, or the continuation of the line above
            dtext = ' '.join(w for x, w in ws if not cols or x < cols[0] or not is_unit_or_rate(w))
            extra = [(x, w) for x, w in ws if cols and x >= cols[0] and is_unit_or_rate(w)]
            if first_x < 15:          # notes, section text, etc.
                current = None; blank = True
                continue
            if dtext.startswith('-'):
                level, label = split_dashes(dtext)
                for k in [k for k in stack if k >= level]: del stack[k]
                stack[level] = label; current = ('group', level); blank = False
                continue
            if not blank and current is not None:
                if current[0] == 'code':
                    r = current[1]
                    if dtext: r['own'] = (r['own'] + ' ' + dtext).strip()
                    for x, w in extra:
                        key = 'mfn' if x >= cols[2] else 'general' if x >= cols[1] else 'unit'
                        r[key] = (r[key] + ' ' + w).strip()
                elif current[0] == 'group':
                    stack[current[1]] = (stack[current[1]] + ' ' + dtext).strip()
                elif current[0] == 'heading':
                    heading_title = (heading_title + ' ' + dtext).strip()
            if heading: heading_titles[heading] = heading_title
            blank = False
    # The second schedule (excise) repeats customs codes and is not a classification.
    rows = [r for r in rows if r['page'] < 1130]
    for r in rows:
        if r['heading'] in heading_titles and heading_titles[r['heading']]:
            r['heading_title'] = heading_titles[r['heading']]
        r['own'] = clean(r['own']); r['parents'] = [clean(p) for p in r['parents'] if clean(p)]
        r['heading_title'] = clean(r['heading_title'])
    return rows, chapters

if __name__ == '__main__':
    rows, chapters = parse(sys.argv[1])
    json.dump({'rows': rows, 'chapters': chapters}, open(sys.argv[2], 'w'), indent=0)
    print(len(rows), 'rows', len(chapters), 'chapters')
