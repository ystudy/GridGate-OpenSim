"""Draws the plan charts the README shows (docs/images/plans/*.svg), with their icons drawn in.

Each chart follows the reader's light or dark mode (GitHub honours prefers-color-scheme in SVG images).
Run: python make_charts.py              rewrites the SVGs next to this script
     python make_charts.py <dir> --previews   also writes always-dark copies and preview.html to <dir>, for checking
The SVGs are committed; after changing this script, run it and commit the SVGs with it.
"""
import os
import sys

FONT = '-apple-system, BlinkMacSystemFont, "Segoe UI", Helvetica, Arial, sans-serif'
W = 800
CW, CH = 170, 130  # default card size

LIGHT = """
.purple .card{fill:#EEEDFE;stroke:#534AB7}.purple{color:#534AB7}.purple .title{fill:#3C3489}.purple .sub{fill:#534AB7}
.gray .card{fill:#F6F5F0;stroke:#888780}.gray{color:#5F5E5A}.gray .title{fill:#2C2C2A}.gray .sub{fill:#5F5E5A}
.teal .card{fill:#E1F5EE;stroke:#0F6E56}.teal{color:#0F6E56}.teal .title{fill:#085041}.teal .sub{fill:#0F6E56}
.net{fill:none;stroke:#888780}.netlabel{fill:#5F5E5A}.wire{stroke:#888780;fill:none}.ah{fill:#888780}
.pill{fill:#FFFFFF;stroke:#B4B2A9}.pilltext{fill:#444441}
.screen{fill:#E6F1FB}.land{fill:#639922}.cardfill{fill:#F6F5F0}
.rtitle{fill:#2C2C2A}.rsub{fill:#5F5E5A}.stripe{fill:#F6F5F0}
.chk{fill:#534AB7}.tick{stroke:#FFFFFF}.purple .num{fill:#FFFFFF}.wave{stroke:#378ADD}.opt{stroke:#534AB7;fill:none}.optick{stroke:#534AB7}.nd{stroke:#B4B2A9}
"""
DARK = """
.purple .card{fill:#26215C;stroke:#AFA9EC}.purple{color:#CECBF6}.purple .title{fill:#EEEDFE}.purple .sub{fill:#CECBF6}
.gray .card{fill:#2C2C2A;stroke:#888780}.gray{color:#D3D1C7}.gray .title{fill:#F1EFE8}.gray .sub{fill:#D3D1C7}
.teal .card{fill:#04342C;stroke:#5DCAA5}.teal{color:#9FE1CB}.teal .title{fill:#E1F5EE}.teal .sub{fill:#9FE1CB}
.net{stroke:#888780}.netlabel{fill:#B4B2A9}.wire{stroke:#9C9A92}.ah{fill:#9C9A92}
.pill{fill:#161B22;stroke:#5F5E5A}.pilltext{fill:#D3D1C7}
.screen{fill:#042C53}.land{fill:#97C459}.cardfill{fill:#2C2C2A}
.rtitle{fill:#F1EFE8}.rsub{fill:#B4B2A9}.stripe{fill:#161B22}
.chk{fill:#AFA9EC}.tick{stroke:#26215C}.purple .num{fill:#26215C}.wave{stroke:#85B7EB}.opt{stroke:#AFA9EC}.optick{stroke:#AFA9EC}.nd{stroke:#5F5E5A}
"""
TEXT = f"""
text{{font-family:{FONT}}}
.title{{font-size:15px;font-weight:600}}.sub{{font-size:13px}}
.netlabel{{font-size:13px;font-weight:500}}.hname{{font-size:13px}}.st{{font-size:14px}}.ss{{font-size:12px}}.bt{{font-size:13px}}
.rtitle{{font-size:14px;font-weight:600}}.rsub{{font-size:12px}}.pilltext{{font-size:12px;font-weight:500}}
"""

# ---------- icons (drawn around a centre point, about 56 x 52 px) ----------
S = 'stroke="currentColor" stroke-linejoin="round" stroke-linecap="round"'


def computer(cx, cy):
    return (f'<rect x="{cx-27}" y="{cy-22}" width="54" height="38" rx="4" class="screen" {S}/>'
            f'<path d="M{cx-18} {cy+12} Q{cx-2} {cy-4} {cx+18} {cy+12} Z" class="land"/>'
            f'<line x1="{cx+4}" y1="{cy+4}" x2="{cx+4}" y2="{cy-6}" {S}/>'
            f'<circle cx="{cx+4}" cy="{cy-10}" r="5" class="land"/>'
            f'<rect x="{cx-4}" y="{cy+16}" width="8" height="6" fill="currentColor"/>'
            f'<rect x="{cx-15}" y="{cy+22}" width="30" height="4" rx="2" fill="currentColor"/>')


def tower(cx, cy):
    out = f'<rect x="{cx-19}" y="{cy-27}" width="38" height="54" rx="4" {S} fill="none"/>'
    for i in range(3):
        y = cy - 20 + i * 15
        out += (f'<rect x="{cx-12}" y="{y}" width="24" height="10" rx="2" {S} stroke-width="1.6" fill="none"/>'
                f'<circle cx="{cx+6}" cy="{y+5}" r="2" class="land"/>')
    return out


def router(cx, cy):
    out = (f'<rect x="{cx-27}" y="{cy-2}" width="54" height="22" rx="5" {S} fill="none"/>'
           f'<line x1="{cx-15}" y1="{cy-2}" x2="{cx-21}" y2="{cy-24}" {S}/>'
           f'<line x1="{cx+15}" y1="{cy-2}" x2="{cx+21}" y2="{cy-24}" {S}/>'
           f'<circle cx="{cx-21}" cy="{cy-25}" r="2.5" fill="currentColor"/>'
           f'<circle cx="{cx+21}" cy="{cy-25}" r="2.5" fill="currentColor"/>')
    for dx in (-14, -5, 4):
        out += f'<circle cx="{cx+dx}" cy="{cy+9}" r="2.2" class="land"/>'
    return out + f'<line x1="{cx+12}" y1="{cy+9}" x2="{cx+19}" y2="{cy+9}" {S}/>'


def _person(cx, cy, k=1.0):
    r, w, top, bot = 7 * k, 13 * k, cy + 1 * k, cy + 17 * k
    return (f'<circle cx="{cx}" cy="{cy-10*k}" r="{r}" {S} class="cardfill"/>'
            f'<path d="M{cx-w} {bot} Q{cx-w} {top} {cx} {top} Q{cx+w} {top} {cx+w} {bot} Z" {S} class="cardfill"/>')


def person(cx, cy):
    return _person(cx, cy + 2)


def people(cx, cy):
    return _person(cx - 17, cy, 0.8) + _person(cx + 17, cy, 0.8) + _person(cx, cy + 5)


def globe(cx, cy):
    return (f'<circle cx="{cx}" cy="{cy}" r="23" {S} fill="none"/>'
            f'<ellipse cx="{cx}" cy="{cy}" rx="10" ry="23" {S} fill="none"/>'
            f'<line x1="{cx-23}" y1="{cy}" x2="{cx+23}" y2="{cy}" {S}/>'
            f'<line x1="{cx-19}" y1="{cy-12}" x2="{cx+19}" y2="{cy-12}" {S} stroke-width="1.5"/>'
            f'<line x1="{cx-19}" y1="{cy+12}" x2="{cx+19}" y2="{cy+12}" {S} stroke-width="1.5"/>')


def cloud(cx, cy):
    return (f'<path d="M{cx-26} {cy+14} H{cx+22} A11 11 0 0 0 {cx+17} {cy-5} '
            f'A15 15 0 0 0 {cx-10} {cy-9} A12 12 0 0 0 {cx-26} {cy+14} Z" {S} fill="none"/>'
            f'<line x1="{cx-14}" y1="{cy-2}" x2="{cx+10}" y2="{cy+10}" {S}/>'
            f'<line x1="{cx+10}" y1="{cy-2}" x2="{cx-14}" y2="{cy+10}" {S}/>')


def house(cx, cy):
    return (f'<path d="M{cx-26} {cy-2} L{cx} {cy-24} L{cx+26} {cy-2}" {S} fill="none"/>'
            f'<path d="M{cx-19} {cy-7} V{cy+22} H{cx+19} V{cy-7}" {S} class="screen"/>'
            f'<rect x="{cx-6}" y="{cy+6}" width="12" height="16" rx="2" fill="currentColor"/>')


def link(cx, cy):
    return (f'<g transform="rotate(-35 {cx} {cy})">'
            f'<rect x="{cx-27}" y="{cy-9}" width="32" height="18" rx="9" {S} fill="none"/>'
            f'<rect x="{cx-5}" y="{cy-9}" width="32" height="18" rx="9" {S} fill="none"/></g>')


def cylinder(cx, cy):
    return (f'<ellipse cx="{cx}" cy="{cy-16}" rx="18" ry="6" {S} fill="none"/>'
            f'<path d="M{cx-18} {cy-16} V{cy+16} A18 6 0 0 0 {cx+18} {cy+16} V{cy-16}" {S} fill="none"/>'
            f'<path d="M{cx-18} {cy} A18 6 0 0 0 {cx+18} {cy}" {S} fill="none"/>')


def island(cx, cy):
    return (f'<path d="M{cx-24} {cy+10} Q{cx-4} {cy-12} {cx+22} {cy+10} Z" class="land"/>'
            f'<line x1="{cx+2}" y1="{cy-2}" x2="{cx+2}" y2="{cy-14}" {S}/>'
            f'<circle cx="{cx+2}" cy="{cy-18}" r="6" class="land"/>'
            f'<path d="M{cx-26} {cy+18} q6 -5 12 0 t12 0 t12 0 t12 0" class="wave" fill="none" stroke-linecap="round"/>')


def browser(cx, cy):
    return (f'<rect x="{cx-26}" y="{cy-20}" width="52" height="40" rx="4" {S} fill="none"/>'
            f'<line x1="{cx-26}" y1="{cy-10}" x2="{cx+26}" y2="{cy-10}" {S}/>'
            + ''.join(f'<circle cx="{cx-20+i*6}" cy="{cy-15}" r="1.6" fill="currentColor"/>' for i in range(3))
            + f'<line x1="{cx-16}" y1="{cy}" x2="{cx+16}" y2="{cy}" {S}/>'
            f'<line x1="{cx-16}" y1="{cy+9}" x2="{cx+6}" y2="{cy+9}" {S}/>')


def download(cx, cy):
    return (f'<line x1="{cx}" y1="{cy-24}" x2="{cx}" y2="{cy+4}" {S}/>'
            f'<path d="M{cx-10} {cy-6} L{cx} {cy+4} L{cx+10} {cy-6}" {S} fill="none"/>'
            f'<path d="M{cx-24} {cy+2} V{cy+18} H{cx+24} V{cy+2}" {S} fill="none"/>')


def small(icon, cx, cy, k):
    return (f'<g transform="translate({cx} {cy}) scale({k}) translate({-cx} {-cy})" stroke-width="{2/k:.2f}">'
            f'{icon(cx, cy)}</g>')


# ---------- building blocks ----------
def card(x, y, cls, icon, title, sub, w=CW, h=CH, dashed=False):
    cx = x + w / 2
    dash = ' stroke-dasharray="6 4"' if dashed else ''
    return (f'<g class="{cls}"><rect x="{x}" y="{y}" width="{w}" height="{h}" rx="12" class="card" stroke-width="1.3"{dash}/>'
            f'<g stroke-width="2">{icon(cx, y + 44)}</g>'
            f'<text x="{cx}" y="{y + 95}" text-anchor="middle" class="title">{title}</text>'
            f'<text x="{cx}" y="{y + 114}" text-anchor="middle" class="sub">{sub}</text></g>')


def net(x, y, w, h, label):
    return (f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="16" class="net" stroke-width="1.4" stroke-dasharray="7 5"/>'
            f'<text x="{x + 18}" y="{y + 26}" class="netlabel">{label}</text>')


def wire(points, dashed=False, both=False):
    d = 'M' + ' L'.join(f'{px} {py}' for px, py in points)
    dash = ' stroke-dasharray="6 4"' if dashed else ''
    start = ' marker-start="url(#ah)"' if both else ''
    return f'<path d="{d}" class="wire" stroke-width="1.8"{dash}{start} marker-end="url(#ah)"/>'


def pill(cx, cy, text):
    w = len(text) * 7 + 18
    return (f'<rect x="{cx - w/2}" y="{cy - 11}" width="{w}" height="22" rx="11" class="pill" stroke-width="1"/>'
            f'<text x="{cx}" y="{cy + 4}" text-anchor="middle" class="pilltext">{text}</text>')


def svg(title, desc, h, body, dark_only=False):
    style = LIGHT + (DARK if dark_only else '@media (prefers-color-scheme: dark){' + DARK + '}') + TEXT
    return (f'<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{h}" viewBox="0 0 {W} {h}" role="img">'
            f'<title>{title}</title><desc>{desc}</desc><style>{style}</style>'
            '<defs><marker id="ah" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" '
            'orient="auto-start-reverse"><path d="M0 0 L10 5 L0 10 z" class="ah"/></marker></defs>'
            f'{body}</svg>\n')


# ---------- the charts ----------
CHARTS = {}

CHARTS['plan-local'] = ('Local plan', 'One computer runs the whole grid; only viewers on the same network can visit.', 420, ''.join([
    net(30, 20, 560, 380, 'Your home or office network'),
    card(225, 60, 'purple', computer, 'This computer', 'Runs the whole grid'),
    card(70, 240, 'gray', person, 'Your viewer', 'Firestorm on your PC'),
    card(380, 240, 'gray', people, "Friends' viewers", 'Same network only'),
    wire([(170, 238), (255, 194)]),
    wire([(450, 238), (365, 194)]),
    card(610, 150, 'gray', cloud, 'Internet', "Can't reach in", dashed=True),
]))

CHARTS['plan-simple-public'] = ('Simple Public Server plan', 'Visitors and other grids reach your router, which forwards ports to the one computer running the whole grid.', 420, ''.join([
    card(20, 40, 'gray', people, 'Visitors', 'From anywhere'),
    card(20, 250, 'teal', globe, 'Other grids', 'Hypergrid, one switch'),
    net(225, 20, 555, 380, 'Your network'),
    card(265, 145, 'gray', router, 'Your router', 'Forwards the ports'),
    card(575, 145, 'purple', computer, 'This computer', 'Runs the whole grid'),
    wire([(192, 110), (263, 180)]),
    wire([(192, 300), (263, 232)], dashed=True, both=True),
    wire([(437, 210), (573, 210)]),
    pill(505, 210, '8002 + regions'),
]))

CHARTS['plan-grid-network'] = ('Grid Asset Server with Region Servers, network only', 'A Grid Asset Server holds accounts, inventory and assets; Region Servers on the same network join it on private port 8003.', 450, ''.join([
    net(30, 20, 740, 410, 'Your network (nobody outside can reach in)'),
    card(300, 60, 'purple', tower, 'Grid Asset Server', 'Accounts, inventory, assets', w=200),
    card(70, 270, 'purple', computer, 'Region Server', 'Regions only'),
    card(315, 270, 'purple', computer, 'Region Server', 'Regions only'),
    card(560, 270, 'purple', computer, 'Region Server', 'Regions only'),
    wire([(170, 268), (330, 194)]),
    wire([(400, 268), (400, 194)]),
    wire([(630, 268), (470, 194)]),
    pill(400, 231, 'private port 8003'),
]))

CHARTS['plan-grid-public'] = ('Grid Asset Server with Region Servers, public', 'Visitors and other grids reach your router; it forwards 8002 to the Grid Asset Server and region ports to each Region Server, which join the Grid Asset Server on 8003.', 800, ''.join([
    card(150, 20, 'gray', people, 'Visitors', 'From anywhere'),
    card(480, 20, 'teal', globe, 'Other grids', 'Hypergrid, one switch'),
    net(30, 190, 740, 590, 'Your network'),
    card(315, 230, 'gray', router, 'Your router', 'Forwards the ports'),
    card(300, 420, 'purple', tower, 'Grid Asset Server', 'Accounts, inventory, assets', w=200),
    card(70, 620, 'purple', computer, 'Region Server', 'Regions only'),
    card(560, 620, 'purple', computer, 'Region Server', 'Regions only'),
    wire([(235, 152), (355, 228)]),
    wire([(565, 152), (445, 228)], dashed=True, both=True),
    wire([(400, 362), (400, 418)]),
    pill(400, 390, '8002'),
    wire([(313, 295), (110, 295), (110, 618)]),
    wire([(487, 295), (690, 295), (690, 618)]),
    pill(110, 460, 'region ports'),
    pill(690, 460, 'region ports'),
    wire([(210, 618), (330, 552)]),
    wire([(590, 618), (470, 552)]),
    pill(270, 585, '8003'),
    pill(530, 585, '8003'),
]))

CHARTS['plan-connected'] = ('Connected Servers on another grid', 'Several Connected Servers in different homes each add regions to a grid someone else runs, such as OSgrid, which keeps the accounts.', 480, ''.join([
    card(40, 30, 'gray', people, 'Visitors', 'Sign in with OSgrid'),
    card(300, 30, 'teal', tower, 'OSgrid', 'Accounts, inventory, assets', w=200),
    wire([(212, 95), (298, 95)]),
    net(30, 250, 350, 200, 'Your home'),
    net(420, 250, 350, 200, "A friend's home"),
    card(105, 290, 'purple', computer, 'Your Connected Server', 'Your regions only', w=200),
    card(495, 290, 'purple', computer, 'Their Connected Server', 'Their regions only', w=200),
    wire([(360, 162), (240, 288)], both=True),
    wire([(440, 162), (560, 288)], both=True),
]))


def prerequisites():
    plans = [('Local', '', house), ('Simple Public', 'Server', globe), ('Grid Asset', 'Server', tower),
             ('Region', 'Server', computer), ('Connected', 'Server', link)]
    # Y needed, P only with a public address, N not needed
    rows = [
        (computer, 'gray', 'A 64-bit computer', 'Debian 12/13 or Windows 10/11', 'YYYYY'),
        (person, 'gray', 'A viewer for each visitor', 'Firestorm or similar', 'YYYYY'),
        (globe, 'gray', 'A public address', 'Public IP or a name for it', 'NYPNY'),
        (router, 'gray', 'Router port forwarding', 'Grid port and region ports', 'NYPPY'),
        (tower, 'purple', 'A Grid Asset Server', 'On your network, same version', 'NNNYN'),
        (person, 'teal', 'An avatar on the other grid', 'Plus its login address', 'NNNNY'),
    ]
    cols = [358, 454, 550, 646, 742]
    out = []
    for cx, (l1, l2, icon) in zip(cols, plans):
        out.append(f'<g class="purple"><rect x="{cx-44}" y="16" width="88" height="112" rx="10" class="card" stroke-width="1.3"/>'
                   f'<g stroke-width="2">{small(icon, cx, 54, 0.8)}</g>'
                   f'<text x="{cx}" y="{100 if l2 else 108}" text-anchor="middle" class="title hname">{l1}</text>'
                   + (f'<text x="{cx}" y="{117}" text-anchor="middle" class="title hname">{l2}</text>' if l2 else '')
                   + '</g>')
    top, rh = 144, 58
    for i, (icon, cls, title, sub, marks) in enumerate(rows):
        y = top + i * rh
        if i % 2 == 0:
            out.append(f'<rect x="20" y="{y}" width="772" height="{rh}" rx="8" class="stripe"/>')
        out.append(f'<g class="{cls}">{small(icon, 52, y + rh/2, 0.55)}</g>'
                   f'<text x="88" y="{y + 25}" class="rtitle">{title}</text>'
                   f'<text x="88" y="{y + 43}" class="rsub">{sub}</text>')
        for cx, m in zip(cols, marks):
            out.append(mark(cx, y + rh / 2, m))
    ly = top + len(rows) * rh + 34
    for x, m, text in ((40, 'Y', 'Needed'), (160, 'P', 'Only with a public address'), (400, 'N', 'Not needed')):
        out.append(mark(x + 11, ly, m) + f'<text x="{x + 30}" y="{ly + 5}" class="rsub">{text}</text>')
    return ('What each plan needs', 'A table of the five plans and what each needs: a 64-bit computer, viewers, a public '
            'address, router port forwarding, a Grid Asset Server, an avatar on another grid.', ly + 30, ''.join(out))


def mark(cx, cy, m):
    tick = f'M{cx-5} {cy} L{cx-1.5} {cy+4} L{cx+5.5} {cy-4}'
    if m == 'Y':
        return (f'<circle cx="{cx}" cy="{cy}" r="11" class="chk"/>'
                f'<path d="{tick}" class="tick" stroke-width="2.4" fill="none" stroke-linecap="round" stroke-linejoin="round"/>')
    if m == 'P':
        return (f'<circle cx="{cx}" cy="{cy}" r="10.5" class="opt" stroke-width="1.6" stroke-dasharray="3 2.5"/>'
                f'<path d="{tick}" class="optick" stroke-width="2.2" fill="none" stroke-linecap="round" stroke-linejoin="round"/>')
    return f'<line x1="{cx-7}" y1="{cy}" x2="{cx+7}" y2="{cy}" class="nd" stroke-width="2.2" stroke-linecap="round"/>'


CHARTS['plan-prerequisites'] = prerequisites()


def block(x, y, icon, title, sub, kind='on', w=208, h=52):
    cls, dash = {'on': ('purple', ''), 'opt': ('purple', ' stroke-dasharray="6 4"'),
                 'off': ('gray', ' stroke-dasharray="6 4"')}[kind]
    op = ' opacity="0.75"' if kind != 'on' else ''
    return (f'<g class="{cls}"{op}><rect x="{x}" y="{y}" width="{w}" height="{h}" rx="8" class="card" stroke-width="1.2"{dash}/>'
            f'{small(icon, x + 26, y + h/2, 0.5)}'
            f'<text x="{x + 52}" y="{y + 22}" class="title bt">{title}</text>'
            f'<text x="{x + 52}" y="{y + 40}" class="sub ss">{sub}</text></g>')


def what_runs():
    machines = [
        ('Local or Simple Public', computer, [
            (cylinder, 'Database', 'MariaDB, everything', 'on'),
            (person, 'Accounts &amp; inventory', 'Robust', 'on'),
            (island, 'Regions', 'OpenSim', 'on'),
            (browser, 'Admin website', 'GridGate, port 8001', 'on')]),
        ('Grid Asset Server', tower, [
            (cylinder, 'Database', 'MariaDB, everything', 'on'),
            (person, 'Accounts &amp; inventory', 'Robust, ports 8002/8003', 'on'),
            (island, 'Regions', 'Optional, not advised', 'opt'),
            (browser, 'Admin website', 'GridGate, port 8001', 'on')]),
        ('Region or Connected Server', computer, [
            (cylinder, 'Database', 'MariaDB, region data only', 'on'),
            (person, 'Accounts &amp; inventory', 'From the other grid', 'off'),
            (island, 'Regions', 'OpenSim', 'on'),
            (browser, 'Admin website', 'GridGate, port 8001', 'on')]),
    ]
    out = [card(220, 16, 'gray', download, 'Same installer on every computer',
                'install.sh on Debian, GridGateSetup.exe on Windows', w=360)]
    out.append(wire([(400, 148), (400, 166)]))
    out.append(f'<g class="teal"><rect x="24" y="168" width="752" height="62" rx="12" class="card" stroke-width="1.3"/>'
               f'<g stroke-width="2">{gear(60, 199)}</g>'
               '<text x="92" y="194" class="title bt">GridGate then sets the computer up for the plan you pick</text>'
               '<text x="92" y="214" class="sub ss">Creates the databases · writes the settings files · '
               'picks free ports · starts each program for you</text></g>')
    top = 260
    for i, (name, icon, blocks) in enumerate(machines):
        x = 24 + i * 258
        out.append(f'<g class="gray"><rect x="{x}" y="{top}" width="236" height="356" rx="14" class="card" stroke-width="1.3"/>'
                   f'{small(icon, x + 118, top + 38, 0.7)}'
                   f'<text x="{x + 118}" y="{top + 86}" text-anchor="middle" class="title">{name}</text></g>')
        for j, (bicon, title, sub, kind) in enumerate(blocks):
            out.append(block(x + 14, top + 104 + j * 62, bicon, title, sub, kind))
        out.append(wire([(x + 118, 232), (x + 118, top - 2)]))
    h = top + 356 + 50
    legend = (('on', 'Runs here'), ('opt', 'Optional'), ('off', 'Not here: comes from another computer'))
    for k, (m, text) in enumerate(legend):
        x = 40 + k * 160
        cls = 'gray' if m == 'off' else 'purple'
        dash = '' if m == 'on' else ' stroke-dasharray="5 3"'
        out.append(f'<g class="{cls}"><rect x="{x}" y="{h-34}" width="26" height="18" rx="4" class="card" stroke-width="1.2"{dash}/></g>'
                   f'<text x="{x + 34}" y="{h-21}" class="rsub">{text}</text>')
    return ('What goes on each computer', 'Every computer gets the same installer; the plan decides what runs: database, '
            'accounts and inventory (Robust), regions (OpenSim) and the admin website.', h, ''.join(out))


def step(x, y, n, title, sub, w=360, h=58):
    return (f'<g class="purple"><rect x="{x}" y="{y}" width="{w}" height="{h}" rx="10" class="card" stroke-width="1.2"/>'
            f'<circle cx="{x + 28}" cy="{y + h/2}" r="13" class="chk"/>'
            f'<text x="{x + 28}" y="{y + h/2 + 5}" text-anchor="middle" class="title st num">{n}</text>'
            f'<text x="{x + 54}" y="{y + 25}" class="title st">{title}</text>'
            f'<text x="{x + 54}" y="{y + 43}" class="sub ss">{sub}</text></g>')


def head(x, y, icon, title, sub, w=360, h=90):
    return (f'<g class="gray"><rect x="{x}" y="{y}" width="{w}" height="{h}" rx="12" class="card" stroke-width="1.3"/>'
            f'{small(icon, x + 50, y + h/2, 0.8)}'
            f'<text x="{x + 96}" y="{y + 40}" class="title">{title}</text>'
            f'<text x="{x + 96}" y="{y + 62}" class="sub">{sub}</text></g>')


def gear(cx, cy):
    import math
    teeth = ''.join(
        f'<line x1="{cx + 10 * math.cos(a):.1f}" y1="{cy + 10 * math.sin(a):.1f}" '
        f'x2="{cx + 15 * math.cos(a):.1f}" y2="{cy + 15 * math.sin(a):.1f}" stroke="currentColor" stroke-width="4.5"/>'
        for a in (k * math.pi / 4 for k in range(8)))
    return (teeth + f'<circle cx="{cx}" cy="{cy}" r="10.5" {S} class="cardfill"/>'
            f'<circle cx="{cx}" cy="{cy}" r="4" fill="currentColor"/>')


def does(x, y, line1, line2, w=420, h=62):
    return (f'<g class="teal"><rect x="{x}" y="{y}" width="{w}" height="{h}" rx="10" class="card" stroke-width="1.2"/>'
            f'<g stroke-width="2">{gear(x + 30, y + h/2)}</g>'
            f'<text x="{x + 58}" y="{y + 27}" class="sub ss">{line1}</text>'
            f'<text x="{x + 58}" y="{y + 45}" class="sub ss">{line2}</text></g>')


def bar(y, icon, title, w=752, h=50):
    return (f'<g class="gray"><rect x="24" y="{y}" width="{w}" height="{h}" rx="10" class="card" stroke-width="1.3"/>'
            f'{small(icon, 56, y + h/2, 0.55)}'
            f'<text x="88" y="{y + 31}" class="title">{title}</text></g>')


def add_computer():
    server = [
        ('Run the installer', 'install.sh or GridGateSetup.exe',
         'Installs MariaDB and everything else it needs, starts the', 'GridGate service, and gives you a setup link'),
        ('Choose “Grid Asset Server”', 'On the first setup page',
         'Plans what this computer runs: database, accounts', 'and inventory (Robust), and the admin website'),
        ('Network or internet?', 'Say who will visit',
         'Finds this computer’s address for you; this is the', 'address the other computers will join'),
        ('Name your world', 'Then make your avatar',
         'Creates the databases, writes every settings file,', 'starts the grid, and makes your owner account'),
        ('Choose your first region', 'Optional on this computer',
         'Picks a free square on the map, builds the region', 'and starts it, showing its progress'),
    ]
    extra = [
        ('Run the installer', 'Same GridGate version',
         'The same install as the server: MariaDB, the', 'GridGate service, and a setup link'),
        ('Choose “Region Server”', 'On the first setup page',
         'Leaves out accounts and inventory: those stay', 'on the Grid Asset Server'),
        ('Type the server’s address', 'Like 192.168.1.5, then Join',
         'Reads the grid’s name, ports, version and hypergrid', 'from the server, shows them, and connects to it'),
        ('Check your network', 'Usually just Continue',
         'Fills in this computer’s address and its first region', 'port, and offers another port if one is taken'),
        ('Type your avatar’s name', 'The one you already have',
         'Makes that avatar the owner of this computer’s', 'regions; no new account or password'),
        ('Choose your first region', 'Name it, pick its content',
         'Picks a free square on the grid’s map, builds the', 'region and starts it, showing its progress'),
        ('Connect with Firestorm', 'Log in and look around',
         'Shows the login address to paste into Firestorm;', 'everyone shares one grid, one map, one inventory'),
    ]
    out = [f'<g class="purple">{small(person, 44, 26, 0.5)}</g><text x="62" y="31" class="netlabel">You do</text>',
           f'<g class="teal"><g stroke-width="2">{small(gear, 378, 26, 0.75)}</g></g>'
           f'<text x="398" y="31" class="netlabel">GridGate does</text>']
    y = 48

    def section(y, icon, title, rows):
        out.append(bar(y, icon, title))
        y += 62
        for i, (t, sub, d1, d2) in enumerate(rows):
            out.append(step(24, y, i + 1, t, sub, w=300, h=62))
            out.append(wire([(326, y + 31), (354, y + 31)]))
            out.append(does(356, y, d1, d2))
            y += 72
        return y + 8

    y = section(y, tower, 'First: the Grid Asset Server', server)
    y = section(y, computer, 'Then: each extra computer (repeat for every Region Server)', extra)
    out.append(f'<text x="400" y="{y + 10}" text-anchor="middle" class="rsub">A Connected Server follows the same steps, '
               'but step 3 is “Connect to the other grid”: OSgrid is one choice.</text>')
    return ('How to add another computer', 'Each step you take, and what GridGate does for you: set up the Grid Asset '
            'Server first, then on each extra computer choose Region Server and type the server’s address.',
            y + 34, ''.join(out))


CHARTS['plan-what-runs'] = what_runs()
CHARTS['plan-add-computer'] = add_computer()


def main(out, previews):
    os.makedirs(out, exist_ok=True)
    for name, (title, desc, h, body) in CHARTS.items():
        for suffix, dark in (('', False), ('.dark', True)) if previews else (('', False),):
            with open(os.path.join(out, name + suffix + '.svg'), 'w', encoding='utf-8', newline='\n') as f:
                f.write(svg(title, desc, h, body, dark_only=dark))
    if not previews:
        return
    rows = ''.join(
        f'<h2>{CHARTS[n][0]}</h2><div class="pair"><div class="light"><img src="{n}.svg"></div>'
        f'<div class="dark"><img src="{n}.dark.svg"></div></div>' for n in CHARTS)
    with open(os.path.join(out, 'preview.html'), 'w', encoding='utf-8') as f:
        f.write('<!DOCTYPE html><html><head><meta charset="utf-8"><title>GridGate plan charts</title><style>'
                'body{font-family:system-ui;margin:24px;background:#eee}h2{font-weight:500}'
                '.pair{display:flex;gap:16px;flex-wrap:wrap}.pair div{padding:16px;border-radius:8px}'
                '.light{background:#fff}.dark{background:#0d1117}img{width:560px;max-width:100%;display:block}'
                f'</style></head><body>{rows}</body></html>')


if __name__ == '__main__':
    args = [a for a in sys.argv[1:] if a != '--previews']
    main(args[0] if args else os.path.dirname(os.path.abspath(__file__)), '--previews' in sys.argv)
