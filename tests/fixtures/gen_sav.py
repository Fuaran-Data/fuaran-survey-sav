"""Independent SPSS .sav fixture writer (struct-based, from the PSPP system-file spec).

Writes, into the folder given as the only argument:
  plain-{le,be}.sav          uncompressed
  bytecode-{le,be}.sav       bytecode-compressed
  bytecode-end-{le,be}.sav   bytecode-compressed, with the 252 end-of-data code
  zlib-{le,be}.zsav          zlib-compressed, tiny blocks so units straddle block boundaries
  expected.csv               the cases rendered as codes
  expected-labels.csv        the cases rendered with value labels and user-missing values blanked
  expected-dictionary.json   the dictionary facts the extension records carry

Every fixture carries the same dictionary: a 300-byte very long string, long names, a UTF-8
encoding record, long string value labels (subtype 21), long string missing values (22), a
document record (type 6), display parameters (11), a variable set (5), file and variable
attributes including roles (17, 18), multiple response sets of all three kinds (7 and 19), and a
64-bit case count (16). Python standard library only; the generated files are committed, so the
build never runs this (see README.md here).
"""
import csv, json, struct, sys, zlib
from datetime import datetime, date

out = sys.argv[1]
EPOCH = datetime(1582, 10, 14)
SYSMIS = -sys.float_info.max

def secs(d):
    if isinstance(d, date) and not isinstance(d, datetime):
        d = datetime(d.year, d.month, d.day)
    return (d - EPOCH).total_seconds()

def fmt(t, w, d=0):
    return (t << 16) | (w << 8) | d

E1 = ''.join(chr(65 + i % 26) for i in range(300))
E4 = 'é' * 150  # 300 UTF-8 bytes: the 255-byte segment boundary splits a character

# Display parameters (subtype 11): measure 1 nominal / 2 ordinal / 3 scale; alignment 0 left /
# 1 right / 2 centre.
NOMINAL, ORDINAL, SCALE = 1, 2, 3
LEFT, RIGHT, CENTRE = 0, 1, 2

# name, short, width, print format, label, missing (code, values), display (measure, width, alignment)
VARS = [
    ("id", "ID", 0, fmt(5, 8, 0), "Respondent ID", (0, []), (SCALE, 8, RIGHT)),
    ("gender", "GENDER", 0, fmt(5, 1, 0), "Gender", (1, [9.0]), (NOMINAL, 5, RIGHT)),
    ("score", "SCORE", 0, fmt(5, 8, 2), None, (-2, [90.0, 99.0]), (SCALE, 10, RIGHT)),
    ("city", "CITY", 8, fmt(1, 8), "City code", (1, [b"XXX     "]), (NOMINAL, 8, LEFT)),
    ("comment", "COMMENT", 20, fmt(1, 20), "Free text", (0, []), (NOMINAL, 20, LEFT)),
    ("essay", "ESSAY", 300, fmt(1, 255), "Long text", (0, []), (NOMINAL, 40, LEFT)),
    ("dob", "DOB", 0, fmt(20, 11), "Date of birth", (0, []), (SCALE, 11, RIGHT)),
    ("ts", "TS", 0, fmt(22, 20), None, (0, []), (SCALE, 20, RIGHT)),
    ("dur", "DUR", 0, fmt(21, 8), None, (0, []), (SCALE, 8, RIGHT)),
    ("household_income_band", "HOUSEHOL", 0, fmt(5, 3), "Income band", (0, []), (ORDINAL, 6, CENTRE)),
    ("mr_a", "MR_A", 0, fmt(5, 1), "Likes A", (0, []), (NOMINAL, 4, RIGHT)),
    ("mr_b", "MR_B", 0, fmt(5, 1), "Likes B", (0, []), (NOMINAL, 4, RIGHT)),
    ("mc_1", "MC_1", 0, fmt(5, 1), "Colour 1", (0, []), (NOMINAL, 4, RIGHT)),
    ("mc_2", "MC_2", 0, fmt(5, 1), "Colour 2", (0, []), (NOMINAL, 4, RIGHT)),
    # Width 10: its missing value lives in subtype 22, not in the variable record.
    ("postcode", "POSTCODE", 10, fmt(1, 10), "Postcode", (0, []), (NOMINAL, 10, LEFT)),
]
S = None
ROWS = [
    [1, 1, 3.14159, "LDN", "café ünïcödé", E1, date(1980, 5, 17), datetime(2021, 3, 4, 5, 6, 7), 3723, 2,
     1, 0, 1, 2, "SW1A 1AA"],
    [2, 2, 95, "NYC", 'say "hi", ok', "short essay", date(1899, 1, 1), datetime(2000, 1, 1, 12), 30, S,
     0, 1, 2, 3, "UNKNOWN"],
    [3, 9, S, "XXX", "", "", S, S, S, 151,
     S, 1, 3, S, ""],
    [1000000, S, -0.5, "  x", "line1\nline2", E4, date(2024, 2, 29), datetime(1970, 1, 1), 360000, -99,
     1, 1, 1, 1, "EC1A 1BB"],
]
EXPECTED = [
    [v[0] for v in VARS],
    ["1", "1", "3.14159", "LDN", "café ünïcödé", E1, "1980-05-17", "2021-03-04 05:06:07", "1:02:03", "2",
     "1", "0", "1", "2", "SW1A 1AA"],
    ["2", "2", "95", "NYC", 'say "hi", ok', "short essay", "1899-01-01", "2000-01-01 12:00:00", "0:00:30", "",
     "0", "1", "2", "3", "UNKNOWN"],
    ["3", "9", "", "XXX", "", "", "", "", "", "151",
     "", "1", "3", "", ""],
    ["1000000", "", "-0.5", "  x", "line1\nline2", E4, "2024-02-29", "1970-01-01 00:00:00", "100:00:00", "-99",
     "1", "1", "1", "1", "EC1A 1BB"],
]
LABELLED = [r[:] for r in EXPECTED]
LABELLED[1][1], LABELLED[1][3], LABELLED[1][4] = "Male", "London", "Unicode row"
LABELLED[1][10:15] = ["Yes", "No", "Red", "Green", "SW1A 1AA"]
LABELLED[2][1], LABELLED[2][2], LABELLED[2][3] = "Female", "", "New York"
LABELLED[2][10:15] = ["No", "Yes", "Green", "Blue", ""]  # UNKNOWN is user-missing (subtype 22)
LABELLED[3][1], LABELLED[3][3] = "", ""
LABELLED[3][10:15] = ["", "Yes", "Blue", "", ""]
LABELLED[4][10:15] = ["Yes", "Yes", "Red", "Red", "EC1A 1BB"]

DOCUMENTS = ["Fixture document line one", "Second line, with a comma"]
WEIGHT = "SCORE"  # the header's weight index names this variable

def write(path, e, compression, zsav=False, end_code=False):
    p = lambda f, *a: struct.pack(e + f, *a)
    enc = lambda s: s.encode("utf-8")
    pad = lambda b, n: b + b" " * (n - len(b))
    # raw variable list: (short, width, print, label, missing, long name, display); a 300-byte
    # very long string = segments of 255 and 48 (PSPP: each segment but the last is 255 wide, the
    # last is w - (n - 1) * 252), and the display record has one entry per segment.
    raws = []
    for name, short, w, pf, label, miss, disp in VARS:
        if w > 255:
            raws.append((short, 255, fmt(1, 255), label, (0, []), name, disp))
            raws.append(("ESSA0", 48, fmt(1, 48), None, (0, []), None, disp))
        else:
            raws.append((short, w, pf, label, miss, name, disp))
    units = sum(1 if w == 0 else (w + 7) // 8 for _, w, *_ in raws)
    hdr = b"$FL3" if zsav else b"$FL2"
    hdr += pad(b"@(#) SPSS DATA FILE fixture", 60)
    index = {}
    pos = 0
    for short, w, *_ in raws:
        pos += 1
        index[short] = pos
        pos += (w + 7) // 8 - 1 if w else 0
    hdr += p("iiiii", 2, units, compression, index[WEIGHT], len(ROWS)) + p("d", 100.0)
    hdr += b"08 Oct 26" + b"12:00:00" + pad(b"fixture", 64) + b"\0\0\0"
    assert len(hdr) == 176
    d = bytearray(hdr)
    for short, w, pf, label, (mc, mv), _, _ in raws:
        d += p("iiiiii", 2, w, 1 if label else 0, mc, pf, pf) + pad(short.encode(), 8)
        if label:
            lb = enc(label)
            d += p("i", len(lb)) + lb + b" " * ((4 - len(lb) % 4) % 4)
        for v in mv:
            d += v if isinstance(v, bytes) else p("d", v)
        for _ in range((w + 7) // 8 - 1 if w else 0):
            d += p("iiiiii", 2, -1, 0, 0, 0, 0) + b" " * 8

    def value_labels(vars, labels):
        d.extend(p("ii", 3, len(labels)))
        for v, l in labels:
            lb = enc(l)
            d.extend((v if isinstance(v, bytes) else p("d", v)) + bytes([len(lb)]) + lb)
            d.extend(b" " * ((8 - (len(lb) + 1) % 8) % 8))
        d.extend(p("ii", 4, len(vars)) + b"".join(p("i", index[v]) for v in vars))

    value_labels(["GENDER"], [(1.0, "Male"), (2.0, "Female"), (9.0, "Refused")])
    value_labels(["CITY"], [(b"LDN     ", "London"), (b"NYC     ", "New York")])
    value_labels(["MR_A", "MR_B"], [(0.0, "No"), (1.0, "Yes")])  # one label set over two variables
    value_labels(["MC_1", "MC_2"], [(1.0, "Red"), (2.0, "Green"), (3.0, "Blue")])

    # Document record (type 6): n lines of 80 bytes.
    d += p("ii", 6, len(DOCUMENTS)) + b"".join(pad(enc(l), 80) for l in DOCUMENTS)

    def ext(sub, size, data):
        d.extend(p("iiii", 7, sub, size, len(data) // size) + data)

    ext(3, 4, p("8i", 20, 0, 0, -1, 1, 1, 2 if e == "<" else 1, 65001))
    ext(4, 8, p("ddd", SYSMIS, sys.float_info.max, -sys.float_info.max))
    # Subtype 7: "$name=C <labelLen> <label> vars" and "$name=D<countedLen> <counted> <labelLen> <label> vars".
    ext(7, 1, enc("$likes=D1 1 12 Things liked mr_a mr_b\n$colours=C 14 Colours chosen mc_1 mc_2\n"))
    # Subtype 11: measure, width, alignment per non-continuation variable record (segments included).
    ext(11, 4, b"".join(p("iii", *disp) for *_, disp in raws))
    ext(5, 1, enc("demographics= gender household_income_band\n"))
    ext(13, 1, enc("\t".join(f"{s}={n}" for s, _, _, _, _, n, _ in raws if n)))
    ext(14, 1, enc("ESSAY=00300\0\t"))
    ext(16, 8, p("qq", 1, len(ROWS)))
    ext(17, 1, enc("source('fixture generator'\n)tags('a'\n'b'\n)"))
    ext(18, 1, enc("id:$@Role('0'\n)/score:$@Role('1'\n)note('free text'\n)/dur:$@Role('3'\n)"))
    # Subtype 19: "$name=E <1|11> <countedLen> <counted> <labelLen> <label> vars"; 11 = label from the
    # first variable's label.
    ext(19, 1, enc("$likes_e=E 11 1 1 0  mr_a mr_b\n"))
    ext(20, 1, b"UTF-8")
    name, val, lab = enc("comment"), pad(enc("café ünïcödé"), 20), enc("Unicode row")
    ext(21, 1, p("i", len(name)) + name + p("iii", 20, 1, len(val)) + val + p("i", len(lab)) + lab)
    # Subtype 22: int32 name length, name, ONE BYTE count, int32 value length, values.
    ext(22, 1, p("i", 8) + b"postcode" + bytes([1]) + p("i", 8) + b"UNKNOWN ")
    d += p("ii", 999, 0)

    # case data as typed 8-byte units
    case_units = []
    for row in ROWS:
        for (name, short, w, *_), v in zip(VARS, row):
            if w == 0:
                if v is None:
                    case_units.append(("n", None))
                else:
                    x = secs(v) if isinstance(v, (date, datetime)) else float(v)
                    case_units.append(("n", x))
            else:
                b = enc(v)
                if w > 255:
                    segs = [(b[:255], 255), (b[255:], 48)]
                else:
                    segs = [(b, w)]
                for sb, sw in segs:
                    sb = pad(sb, (sw + 7) // 8 * 8)
                    case_units += [("s", sb[i:i + 8]) for i in range(0, len(sb), 8)]

    if compression == 0:
        body = b"".join(p("d", SYSMIS if x is None else x) if k == "n" else x for k, x in case_units)
        d += body
    else:
        stream = bytearray()
        cmds, raw = [], bytearray()
        def flush():
            stream.extend(bytes(cmds + [0] * (8 - len(cmds))) + raw)
            cmds.clear(); raw.clear()
        for k, x in case_units:
            if k == "n" and x is None:
                cmds.append(255)
            elif k == "n" and x == int(x) and -99 <= x <= 151:
                cmds.append(int(x) + 100)
            elif k == "s" and x == b" " * 8:
                cmds.append(254)
            else:
                cmds.append(253); raw.extend(p("d", x) if k == "n" else x)
            if len(cmds) == 8:
                flush()
        if end_code:
            cmds.append(252)
        if cmds:
            flush()
        if not zsav:
            d += stream
        else:
            zheader_ofs = len(d)
            block = 64  # tiny blocks: units straddle block boundaries
            chunks = [bytes(stream[i:i + block]) for i in range(0, len(stream), block)]
            comp = [zlib.compress(c) for c in chunks]
            data_ofs = zheader_ofs + 24
            ztrailer_ofs = data_ofs + sum(len(c) for c in comp)
            trailer = p("qqii", -100, 0, block, len(comp))
            u, c_ofs = zheader_ofs, data_ofs
            for raw_c, cc in zip(chunks, comp):
                trailer += p("qqii", u, c_ofs, len(raw_c), len(cc))
                u += len(raw_c); c_ofs += len(cc)
            d += p("qqq", zheader_ofs, ztrailer_ofs, len(trailer))
            for cc in comp:
                d += cc
            d += trailer
    open(path, "wb").write(bytes(d))

for name, rows in (("expected.csv", EXPECTED), ("expected-labels.csv", LABELLED)):
    with open(f"{out}/{name}", "w", newline="", encoding="utf-8") as f:
        csv.writer(f, lineterminator="\r\n").writerows(rows)

MEASURE = {NOMINAL: "Nominal", ORDINAL: "Ordinal", SCALE: "Scale"}
ALIGN = {LEFT: "AlignLeft", RIGHT: "AlignRight", CENTRE: "AlignCentre"}
dictionary = {
    "cases": len(ROWS),
    "weight": next(v[0] for v in VARS if v[1] == WEIGHT),
    "productName": "@(#) SPSS DATA FILE fixture",
    "fileLabel": "fixture",
    "variables": [v[0] for v in VARS],
    "longNames": {"HOUSEHOL": "household_income_band"},
    "veryLongStrings": {"essay": 300},
    "documents": DOCUMENTS,
    "fileAttributes": {"source": ["fixture generator"], "tags": ["a", "b"]},
    "variableAttributes": {"score": {"note": ["free text"]}},
    "roles": {"id": "Input", "score": "Target", "dur": "NoRole"},
    "variableSets": [{"name": "demographics", "variables": ["gender", "household_income_band"]}],
    "mrSets": [
        {"name": "$likes", "kind": "D", "countedValue": "1", "labelSource": "explicit",
         "label": "Things liked", "extended": False, "variables": ["mr_a", "mr_b"]},
        {"name": "$colours", "kind": "C", "label": "Colours chosen", "extended": False,
         "variables": ["mc_1", "mc_2"]},
        {"name": "$likes_e", "kind": "D", "countedValue": "1", "labelSource": "firstVariable",
         "label": "Likes A", "extended": True, "variables": ["mr_a", "mr_b"]},
    ],
    "display": {v[0]: {"measure": MEASURE[v[6][0]], "width": v[6][1], "alignment": ALIGN[v[6][2]]} for v in VARS},
    "longStringMissing": {"postcode": ["UNKNOWN"]},
    "longStringLabels": {"comment": {"café ünïcödé": "Unicode row"}},
    "sharedLabelSets": {"mr_a": {"0": "No", "1": "Yes"}, "mr_b": {"0": "No", "1": "Yes"},
                        "mc_1": {"1": "Red", "2": "Green", "3": "Blue"}, "mc_2": {"1": "Red", "2": "Green", "3": "Blue"}},
}
with open(f"{out}/expected-dictionary.json", "w", newline="\n", encoding="utf-8") as f:
    json.dump(dictionary, f, indent=2, ensure_ascii=False)
    f.write("\n")

for e, en in (("<", "le"), (">", "be")):
    write(f"{out}/plain-{en}.sav", e, 0)
    write(f"{out}/bytecode-{en}.sav", e, 1)
    write(f"{out}/bytecode-end-{en}.sav", e, 1, end_code=True)
    write(f"{out}/zlib-{en}.zsav", e, 2, zsav=True)
print("fixtures written")
