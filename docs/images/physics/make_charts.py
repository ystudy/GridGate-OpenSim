"""Draws the physics engine chart the README shows (docs/images/physics/physics-engines.svg).

It uses the plan charts' colours, fonts and light/dark switch (../plans/make_charts.py), so the two sets match.
The numbers come from the bench runs on the 2-vCPU Debian 12 test VM, written up in
docs/superpowers/specs/2026-11-06-rapier-early-results.md (busy regions, DragonFyre start-up) and
ThirdParty/ubODE/README.md (the start-up fix). Memory is the region's RSS 12 minutes after DragonFyre starts
from the database (past the 6-minute asset memory cache), three runs per engine of build 5572142 on 2026-11-15:
ubODE 491-501 MB, Rapier 634-667 MB (the early results' 1.1 GB came before Rapier's memory fixes).
The freeze and wake numbers are from the region freeze tests,
which are not a GridGate feature yet.
Run: python make_charts.py              rewrites the SVG next to this script
     python make_charts.py <dir> --previews   also writes an always-dark copy and preview.html to <dir>
The SVG is committed; after changing this script, run it and commit the SVG with it.
"""
import importlib.util
import os
import sys

# Load the plan charts' styles by path: this script has the same file name, so a plain import would find itself.
_spec = importlib.util.spec_from_file_location(
    'plan_charts', os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'plans', 'make_charts.py'))
_plans = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_plans)
DARK, LIGHT, TEXT, W = _plans.DARK, _plans.LIGHT, _plans.TEXT, _plans.W

# Bar colours, checked with the dataviz palette validator against white and GitHub's dark #0d1117.
BARS_LIGHT = """
.bu{fill:#534AB7}.br{fill:#1D9E75}.bo{fill:#B4B2A9}.grid{stroke:#EDEBE5}.limit{stroke:#A32D2D}.limtext{fill:#A32D2D}
.val{fill:#2C2C2A}.vsub{fill:#5F5E5A}.axis{fill:#5F5E5A}
"""
BARS_DARK = """
.bu{fill:#7F77DD}.br{fill:#1D9E75}.bo{fill:#5F5E5A}.grid{stroke:#21262D}.limit{stroke:#E57373}.limtext{fill:#E57373}
.val{fill:#F1EFE8}.vsub{fill:#B4B2A9}.axis{fill:#B4B2A9}
"""
BARS_TEXT = """
.val{font-size:12px;font-weight:600}.vsub{font-size:12px}.axis{font-size:11px}.limtext{font-size:11px;font-weight:500}
.sect{font-size:15px;font-weight:600}
"""

X0, X1 = 280, 700           # where bars start and where the scale's top value lands
BH, GAP = 14, 2             # bar height, gap between a row's two bars


def bar(x0, y, w, cls, tip):
    """A bar anchored at x0 with a 4 px rounded data end. Very small values still show as a 2 px sliver."""
    w = max(w, 2)
    r = min(4, w / 2, BH / 2)
    x1 = x0 + w
    d = (f'M{x0} {y} H{x1 - r:.1f} Q{x1:.1f} {y} {x1:.1f} {y + r} V{y + BH - r} '
         f'Q{x1:.1f} {y + BH} {x1 - r:.1f} {y + BH} H{x0} Z')
    return f'<path d="{d}" class="{cls}"><title>{tip}</title></path>'


def scale(top):
    return lambda v: (X1 - X0) * v / top


def axis(y_top, y_bot, top, step, unit):
    sx = scale(top)
    out = []
    v = 0
    while v <= top + 1e-9:
        x = X0 + sx(v)
        out.append(f'<line x1="{x:.1f}" y1="{y_top}" x2="{x:.1f}" y2="{y_bot}" class="grid" stroke-width="1"/>'
                   f'<text x="{x:.1f}" y="{y_bot + 16}" text-anchor="middle" class="axis">{v:g}{unit}</text>')
        v += step
    return ''.join(out)


def header(y, title, sub):
    return (f'<text x="24" y="{y}" class="rtitle sect">{title}</text>'
            f'<text x="24" y="{y + 20}" class="rsub">{sub}</text>')


def row_label(y, title, sub):
    return (f'<text x="24" y="{y}" class="rtitle">{title}</text>'
            + (f'<text x="24" y="{y + 17}" class="rsub">{sub}</text>' if sub else ''))


def value(x, y, text, note=''):
    return (f'<text x="{x + 8:.1f}" y="{y + BH - 3}" class="val">{text}'
            + (f'<tspan class="vsub" dx="6">{note}</tspan>' if note else '') + '</text>')


def legend(y):
    out = []
    x = 24
    for cls, text in (('bu', 'ubODE, the default'), ('br', 'Rapier, experimental'), ('bo', 'ubODE before GridGate’s fix')):
        out.append(f'<rect x="{x}" y="{y - 11}" width="14" height="14" rx="3" class="{cls}"/>'
                   f'<text x="{x + 22}" y="{y + 1}" class="rsub">{text}</text>')
        x += 22 + len(text) * 6.4 + 28
    return ''.join(out)


def chart():
    out = [legend(28)]

    # ---- 1. A busy region: physics milliseconds per frame ----
    y = 76
    out.append(header(y, 'Running a busy region', 'Physics time in each 91 ms frame, lower is better. '
                      'Above one frame, the whole region slows down.'))
    top = 130
    sx = scale(top)
    rows = [
        ('100 objects moving', 'A quiet region', 1.5, 3.0, ''),
        ('500 objects in a pile', '', 48.4, 10.9, ''),
        ('1,000 objects moving', '', 29.3, 26.5, ''),
        ('2,000 objects moving', 'ubODE slows the region to ¼ speed', 121.4, 57.9, ''),
        ('DragonFyre and a pile', '~5,000 meshes, 500 objects', 54.8, 14.2, ''),
    ]
    y0 = y + 58
    rh = 50
    out.append(axis(y0 - 6, y0 + len(rows) * rh - 10, top, 20, ''))
    for i, (title, sub, u, r, note) in enumerate(rows):
        ry = y0 + i * rh
        out.append(row_label(ry + 12, title, sub))
        out.append(bar(X0, ry, sx(u), 'bu', f'{title}: ubODE {u:g} ms'))
        out.append(value(X0 + sx(u), ry, f'{u:g} ms', note))
        out.append(bar(X0, ry + BH + GAP, sx(r), 'br', f'{title}: Rapier {r:g} ms'))
        out.append(value(X0 + sx(r), ry + BH + GAP, f'{r:g} ms'))
    xf = X0 + sx(91)
    yb = y0 + len(rows) * rh - 10
    out.append(f'<line x1="{xf:.1f}" y1="{y0 - 14}" x2="{xf:.1f}" y2="{yb}" class="limit" stroke-width="1.5" '
               'stroke-dasharray="5 4"/>'
               f'<text x="{xf - 6:.1f}" y="{y0 - 18}" text-anchor="end" class="limtext">one frame, 91 ms</text>')
    out.append(f'<text x="{X0}" y="{yb + 34}" class="axis">milliseconds</text>')

    # ---- 2 and 3 share one seconds scale, so the cold start and the wake-up can be compared ----
    top = 20
    sx = scale(top)

    y = yb + 78
    out.append(header(y, 'Starting a mesh-heavy region', 'DragonFyre: 4,090 objects and about 5,000 meshes. '
                      'Seconds until the region is ready, lower is better.'))
    rows = [
        ('ubODE before the fix', 'The OpenSim original', 'bo', 19.7, '19.7 s', ''),
        ('ubODE in GridGate', '', 'bu', 5.3, '5.3 s', 'about 500 MB of memory'),
        ('Rapier', '', 'br', 7.8, '7.8 s', 'about 650 MB of memory'),
    ]
    y0 = y + 44
    rh = 44
    yb2 = y0 + len(rows) * rh - 10
    out.append(axis(y0 - 6, yb2, top, 5, ' s'))
    for i, (title, sub, cls, v, text, note) in enumerate(rows):
        ry = y0 + i * rh
        out.append(row_label(ry + 12, title, sub))
        out.append(bar(X0, ry, sx(v), cls, f'{title}: {text}'))
        out.append(value(X0 + sx(v), ry, text, note))

    y = yb2 + 62
    out.append(header(y, 'Freezing a region and waking it', 'Tested on the bench, not in GridGate yet. '
                      'A frozen region uses no CPU and gives most of its memory back.'))
    rows = [
        ('Start it from cold', 'ubODE in GridGate', 5.3, '5.3 s', ''),
        ('Freeze it', 'While it runs', 0.005, '0.005 s', 'memory 468 MB → 82 MB'),
        ('Wake it', 'When a visitor arrives', 0.015, '0.015 s', 'the teleport lands as usual'),
    ]
    y0 = y + 44
    rh = 44
    yb3 = y0 + len(rows) * rh - 10
    out.append(axis(y0 - 6, yb3, top, 5, ' s'))
    for i, (title, sub, v, text, note) in enumerate(rows):
        ry = y0 + i * rh
        out.append(row_label(ry + 12, title, sub))
        out.append(bar(X0, ry, sx(v), 'bu', f'{title}: {text}'))
        out.append(value(X0 + sx(v), ry, text, note))

    h = yb3 + 58
    out.append(f'<text x="400" y="{h - 14}" text-anchor="middle" class="axis">Measured on a small test computer '
               '(2 cores, 2 GB). Bigger computers are faster, but the differences stay.</text>')
    desc = ('Physics engines compared. Busy region, physics milliseconds per 91 ms frame, ubODE then Rapier: '
            '100 objects moving 1.5 and 3.0; 500 in a pile 48.4 and 10.9; 1,000 moving 29.3 and 26.5; '
            '2,000 moving 121.4 (region at a quarter speed) and 57.9; DragonFyre with a 500-object pile 54.8 and 14.2. '
            'DragonFyre start-up: ubODE before the fix 19.7 s, ubODE in GridGate 5.3 s with about 500 MB, '
            'Rapier 7.8 s with about 650 MB. Freeze test (not in GridGate yet): cold start 5.3 s, freeze 0.005 s, '
            'wake 0.015 s, memory 468 MB to 82 MB while frozen.')
    return 'Physics engines: busy regions, start-up, freeze and wake', desc, h, ''.join(out)


def svg(title, desc, h, body, dark_only=False):
    dark = DARK + BARS_DARK
    style = (LIGHT + BARS_LIGHT + (dark if dark_only else '@media (prefers-color-scheme: dark){' + dark + '}')
             + TEXT + BARS_TEXT)
    return (f'<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{h}" viewBox="0 0 {W} {h}" role="img">'
            f'<title>{title}</title><desc>{desc}</desc><style>{style}</style>{body}</svg>\n')


def main(out, previews):
    os.makedirs(out, exist_ok=True)
    title, desc, h, body = chart()
    for suffix, dark in (('', False), ('.dark', True)) if previews else (('', False),):
        with open(os.path.join(out, 'physics-engines' + suffix + '.svg'), 'w', encoding='utf-8', newline='\n') as f:
            f.write(svg(title, desc, h, body, dark_only=dark))
    if previews:
        with open(os.path.join(out, 'preview.html'), 'w', encoding='utf-8') as f:
            f.write('<!DOCTYPE html><html><head><meta charset="utf-8"><title>GridGate physics chart</title><style>'
                    'body{font-family:system-ui;margin:24px;background:#eee}.pair{display:flex;gap:16px;flex-wrap:wrap}'
                    '.pair div{padding:16px;border-radius:8px}.light{background:#fff}.dark{background:#0d1117}'
                    'img{width:800px;max-width:100%;display:block}</style></head><body><div class="pair">'
                    '<div class="light"><img src="physics-engines.svg"></div>'
                    '<div class="dark"><img src="physics-engines.dark.svg"></div></div></body></html>')


if __name__ == '__main__':
    args = [a for a in sys.argv[1:] if a != '--previews']
    main(args[0] if args else os.path.dirname(os.path.abspath(__file__)), '--previews' in sys.argv)
