# 9 rows. Baseline after row 6: rows 0-6 sit on it, rows 7-8 are the descender.
# Lower case x-height starts at row 2. '.' is off, '#' is on.
A = {}
def g(ch, art):
    rows = [r for r in art.strip('\n').split('\n')]
    w = max(len(r) for r in rows)
    rows = [r.ljust(w, '.') for r in rows]
    while len(rows) < 9:
        rows.append('.' * w)
    assert len(rows) == 9, (ch, len(rows))
    A[ch] = rows

g(' ', "...\n...\n...\n...\n...\n...\n...")
g('A', ".##.\n#..#\n#..#\n#..#\n####\n#..#\n#..#")
g('B', "###.\n#..#\n#..#\n###.\n#..#\n#..#\n###.")
g('C', ".###\n#...\n#...\n#...\n#...\n#...\n.###")
g('D', "###.\n#..#\n#..#\n#..#\n#..#\n#..#\n###.")
g('E', "####\n#...\n#...\n###.\n#...\n#...\n####")
g('F', "####\n#...\n#...\n###.\n#...\n#...\n#...")
g('G', ".###\n#...\n#...\n#.##\n#..#\n#..#\n.###")
g('H', "#..#\n#..#\n#..#\n####\n#..#\n#..#\n#..#")
g('I', "#\n#\n#\n#\n#\n#\n#")
g('J', "..#\n..#\n..#\n..#\n..#\n#.#\n.#.")
g('K', "#..#\n#.#.\n##..\n##..\n#.#.\n#.#.\n#..#")
g('L', "#...\n#...\n#...\n#...\n#...\n#...\n####")
g('M', "#...#\n##.##\n#.#.#\n#.#.#\n#...#\n#...#\n#...#")
g('N', "#..#\n##.#\n##.#\n#.##\n#.##\n#..#\n#..#")
g('O', ".##.\n#..#\n#..#\n#..#\n#..#\n#..#\n.##.")
g('P', "###.\n#..#\n#..#\n###.\n#...\n#...\n#...")
g('Q', ".##.\n#..#\n#..#\n#..#\n#.##\n#.#.\n.###")
g('R', "###.\n#..#\n#..#\n###.\n#.#.\n#..#\n#..#")
g('S', ".###\n#...\n#...\n.##.\n...#\n...#\n###.")
g('T', "#####\n..#..\n..#..\n..#..\n..#..\n..#..\n..#..")
g('U', "#..#\n#..#\n#..#\n#..#\n#..#\n#..#\n.##.")
g('V', "#...#\n#...#\n#...#\n#...#\n.#.#.\n.#.#.\n..#..")
g('W', "#...#\n#...#\n#...#\n#.#.#\n#.#.#\n##.##\n#...#")
g('X', "#...#\n.#.#.\n.#.#.\n..#..\n.#.#.\n.#.#.\n#...#")
g('Y', "#...#\n.#.#.\n.#.#.\n..#..\n..#..\n..#..\n..#..")
g('Z', "#####\n....#\n...#.\n..#..\n.#...\n#....\n#####")

g('a', "....\n....\n.##.\n...#\n.###\n#..#\n.###")
g('b', "#...\n#...\n###.\n#..#\n#..#\n#..#\n###.")
g('c', "....\n....\n.###\n#...\n#...\n#...\n.###")
g('d', "...#\n...#\n.###\n#..#\n#..#\n#..#\n.###")
g('e', "....\n....\n.##.\n#..#\n####\n#...\n.###")
g('f', "..##\n.#..\n####\n.#..\n.#..\n.#..\n.#..")
g('g', "....\n....\n.###\n#..#\n#..#\n.###\n...#\n#..#\n.##.")
g('h', "#...\n#...\n###.\n#..#\n#..#\n#..#\n#..#")
g('i', "#\n.\n#\n#\n#\n#\n#")
g('j', ".#\n..\n.#\n.#\n.#\n.#\n.#\n.#\n#.")
g('k', "#...\n#...\n#..#\n#.#.\n##..\n#.#.\n#..#")
g('l', "##\n.#\n.#\n.#\n.#\n.#\n.#")
g('m', ".....\n.....\n##.#.\n#.#.#\n#.#.#\n#.#.#\n#.#.#")
g('n', "....\n....\n###.\n#..#\n#..#\n#..#\n#..#")
g('o', "....\n....\n.##.\n#..#\n#..#\n#..#\n.##.")
g('p', "....\n....\n###.\n#..#\n#..#\n###.\n#...\n#...\n#...")
g('q', "....\n....\n.###\n#..#\n#..#\n.###\n...#\n...#\n...#")
g('r', "....\n....\n#.##\n##..\n#...\n#...\n#...")
g('s', "....\n....\n.###\n#...\n.##.\n...#\n###.")
g('t', ".#..\n.#..\n####\n.#..\n.#..\n.#..\n..##")
g('u', "....\n....\n#..#\n#..#\n#..#\n#..#\n.###")
g('v', "#...#\n#...#\n#...#\n.#.#.\n.#.#.\n..#..\n..#..")
A['v'] = A['v'][2:] + ['.....', '.....']
A['v'] = [ '.....', '.....', '#...#', '#...#', '.#.#.', '.#.#.', '..#..', '.....', '.....' ]
g('w', ".....\n.....\n#...#\n#.#.#\n#.#.#\n#.#.#\n.#.#.")
g('x', "....\n....\n#..#\n.##.\n.##.\n.##.\n#..#")
g('y', "....\n....\n#..#\n#..#\n#..#\n.###\n...#\n#..#\n.##.")
g('z', "....\n....\n####\n...#\n.##.\n#...\n####")

g('0', ".##.\n#..#\n#.##\n##.#\n#..#\n#..#\n.##.")
g('1', ".#.\n##.\n.#.\n.#.\n.#.\n.#.\n###")
g('2', ".##.\n#..#\n...#\n..#.\n.#..\n#...\n####")
g('3', "###.\n...#\n...#\n.##.\n...#\n...#\n###.")
g('4', "..##\n.#.#\n#..#\n####\n...#\n...#\n...#")
g('5', "####\n#...\n###.\n...#\n...#\n#..#\n.##.")
g('6', ".##.\n#...\n#...\n###.\n#..#\n#..#\n.##.")
g('7', "####\n...#\n..#.\n..#.\n.#..\n.#..\n.#..")
g('8', ".##.\n#..#\n#..#\n.##.\n#..#\n#..#\n.##.")
g('9', ".##.\n#..#\n#..#\n.###\n...#\n...#\n.##.")

g('.', ".\n.\n.\n.\n.\n.\n#")
g(',', ".\n.\n.\n.\n.\n.\n#\n#\n.")
g(':', ".\n.\n#\n.\n.\n#\n.")
g(';', ".\n.\n#\n.\n.\n#\n#\n.\n.")
g('-', "...\n...\n...\n###\n...\n...\n...")
g('/', "...#\n...#\n..#.\n..#.\n.#..\n#...\n#...")
g('(', ".#\n#.\n#.\n#.\n#.\n#.\n.#")
g(')', "#.\n.#\n.#\n.#\n.#\n.#\n#.")
g('%', "#..#\n#..#\n...#\n..#.\n.#..\n#..#\n#..#")
g('@', ".###.\n#...#\n#.##.\n#.#.#\n#.##.\n#....\n.###.")
g('+', "...\n...\n.#.\n###\n.#.\n...\n...")
g('=', "....\n....\n####\n....\n####\n....\n....")
g('!', "#\n#\n#\n#\n#\n.\n#")
g('?', ".##.\n#..#\n...#\n..#.\n.#..\n....\n.#..")
g('_', "....\n....\n....\n....\n....\n....\n####")
g("'", "#\n#\n.\n.\n.\n.\n.")
g('#', ".#.#.\n.#.#.\n#####\n.#.#.\n#####\n.#.#.\n.#.#.")
g('*', ".....\n#.#.#\n.###.\n#####\n.###.\n#.#.#\n.....")
g('<', "..#\n.#.\n#..\n#..\n#..\n.#.\n..#")
g('>', "#..\n.#.\n..#\n..#\n..#\n.#.\n#..")
g('[', "##\n#.\n#.\n#.\n#.\n#.\n##")
g(']', "##\n.#\n.#\n.#\n.#\n.#\n##")
g('"', "#.#\n#.#\n...\n...\n...\n...\n...")
g('$', ".#..\n.###\n#.#.\n.##.\n..##\n###.\n..#.")
g('&', ".##.\n#..#\n#.#.\n.#..\n#.#.\n#..#\n.##.")
g('^', ".#.\n#.#\n...\n...\n...\n...\n...")
g('\\', "#...\n#...\n.#..\n.#..\n..#.\n...#\n...#")
g('{', ".##\n.#.\n.#.\n#..\n.#.\n.#.\n.##")
g('}', "##.\n.#.\n.#.\n..#\n.#.\n.#.\n##.")
g('|', "#\n#\n#\n#\n#\n#\n#")
g('~', "....\n....\n.#.#\n#.#.\n....\n....\n....")
g('`', "#.\n.#\n..\n..\n..\n..\n..")

missing = [chr(c) for c in range(32, 127) if chr(c) not in A]
assert not missing, missing

out = []
for c in range(32, 127):
    rows = A[chr(c)]
    w = len(rows[0])
    assert w <= 8, (chr(c), w)
    bits = []
    for r in rows:
        v = 0
        for i, px in enumerate(r):
            if px == '#':
                v |= 0x80 >> i
        bits.append(v)
    label = chr(c)
    if label in ('\\',):
        label = '\\\\'
    out.append("    { %d, { %s } },  /* %s */" % (w, ", ".join("0x%02x" % b for b in bits), label))

open('source/video/rc_font_prop.h', 'w').write("""/*
 * ripcord-ps3 - a proportional 9-row font for the overlay's words.
 *
 * WHY A SECOND FONT. The 5x7 in rc_font5x7.h is monospaced and upper case only, which is right for
 * numbers - a column of figures has to hold still while the figures change, and a digit that is one
 * pixel narrower than its neighbour makes the whole row shuffle every time it ticks. It is wrong for
 * words: fixed-pitch prose reads like a teletype, and every label on the panel is prose.
 *
 * So the panel uses both, on purpose. Words are set in this; anything that MOVES is set in the
 * monospaced one and right-aligned. That division is the entire reason there are two.
 *
 * Original to this project, drawn here as ASCII art and converted by
 * tools-side script rather than taken from any existing font. No file is redistributed and no
 * existing typeface's outlines or bitmaps are reproduced.
 *
 * NINE ROWS, BASELINE AFTER ROW 6. Rows 0-6 carry capitals and ascenders, lower case x-height starts
 * at row 2, and rows 7-8 are the descender - so g, j, p, q and y hang below the line like they should
 * instead of being squashed up into the body, which is what makes a small bitmap font look cheap.
 *
 * One byte a row, MOST significant bit on the left, and each glyph carries its own width. Codes 32..126.
 */
#ifndef RC_FONT_PROP_H
#define RC_FONT_PROP_H

#define RC_PROP_ROWS      9
#define RC_PROP_BASELINE  7
#define RC_PROP_FIRST     32
#define RC_PROP_LAST      126
#define RC_PROP_GAP       1

typedef struct {
    unsigned char width;
    unsigned char row[RC_PROP_ROWS];
} rc_prop_glyph;

static const rc_prop_glyph kFontProp[RC_PROP_LAST - RC_PROP_FIRST + 1] = {
%s
};

#endif /* RC_FONT_PROP_H */
""" % "\n".join(out))
print("proportional glyphs:", len(out))
