// The world map page (spec 2026-11-20-public-map-design): map-core.js draws the grid's own regions from /world/regions; this
// file adds the card a click opens, with how many people are there and a hop:// teleport link to the spot clicked. Region
// names only ever go in as text. Hand-written, no library; needs map-core.js.
(function () {
    var POLL_MS = 30000, SQUARE_M = 256, LAND_Z = 25, COPIED_MS = 2000;

    function start(map) {
        var card = document.getElementById("world-card");
        var nameEl = document.getElementById("world-card-name");
        var peopleEl = document.getElementById("world-card-people");
        var teleport = document.getElementById("world-teleport");
        var linkEl = document.getElementById("world-link");
        var copy = document.getElementById("world-copy");
        var unavailable = document.getElementById("map-unavailable");
        var copyWords = copy.textContent;
        var teleportStart = "";
        var openName = null;

        // A sign-in that ended (or Terms that changed) gives 401: the page itself says what to do next.
        function load(url) {
            return fetch(url, { headers: { Accept: "application/json" }, credentials: "same-origin" }).then(function (r) {
                if (r.status === 401) {
                    window.location.reload();
                    throw new Error("signed out");
                }
                if (!r.ok) throw new Error("HTTP " + r.status);
                return r.json();
            });
        }

        var view = startMapView(map, {
            tilesUrl: map.getAttribute("data-tiles"),
            regionsUrl: map.getAttribute("data-regions"),
            pollMs: POLL_MS,
            load: load,
            regionsOf: function (json) {
                teleportStart = json.teleport || "";
                unavailable.hidden = json.available !== false;
                return json.regions || [];
            },
            keyOf: function (r) { return r.name; },
            markerClass: function (r) { return r.name === openName ? "selected" : ""; },
            markerTitle: function (r) { return r.name; },
            zoomIn: document.getElementById("zoom-in"),
            zoomOut: document.getElementById("zoom-out"),
            fitFirst: function () { view.fitAll(view.regions()); },
            onRegions: function () {
                // A region that went away takes its card with it; one still there shows its new count.
                var r = openName !== null && view.regionByKey(openName);
                if (openName !== null && !r) close();
                else if (r) showPeople(r);
            },
            onClick: function (p, region) {
                if (region) open(region, view.toWorld(p.x, p.y));
                else close();
            }
        });

        function clamp(v, lo, hi) { return Math.max(lo, Math.min(hi, v)); }

        function showPeople(r) {
            peopleEl.hidden = r.people === null || r.people === undefined;
            peopleEl.textContent = peopleEl.hidden ? "" : map.getAttribute("data-people").replace("{0}", r.people);
        }

        // The spot clicked, in metres inside the region, for the viewer to land on (it puts the avatar on the ground).
        function open(r, at) {
            var w = r.w || 1, h = r.h || 1;
            var mx = clamp(Math.round((at.x - r.x) * SQUARE_M), 0, w * SQUARE_M - 1);
            var my = clamp(Math.round((at.y - r.y) * SQUARE_M), 0, h * SQUARE_M - 1);
            var link = teleportStart + encodeURIComponent(r.name) + "/" + mx + "/" + my + "/" + LAND_Z;
            openName = r.name;
            nameEl.textContent = r.name;
            showPeople(r);
            teleport.href = link;
            linkEl.textContent = link;
            copy.textContent = copyWords;
            card.hidden = false;
            view.restyle();
        }

        function close() {
            if (openName === null) return;
            openName = null;
            card.hidden = true;
            view.restyle();
        }

        // Copies the link; where the browser won't, the link is selected for Ctrl+C.
        copy.addEventListener("click", function () {
            var text = linkEl.textContent;
            function selected() {
                var range = document.createRange();
                range.selectNodeContents(linkEl);
                var sel = window.getSelection();
                sel.removeAllRanges();
                sel.addRange(range);
                copy.textContent = map.getAttribute("data-copy-selected");
            }
            if (!navigator.clipboard || !window.isSecureContext) { selected(); return; }
            navigator.clipboard.writeText(text).then(function () {
                copy.textContent = map.getAttribute("data-copied");
                setTimeout(function () { copy.textContent = copyWords; }, COPIED_MS);
            }, selected);
        });

        document.getElementById("world-card-close").addEventListener("click", close);
        document.addEventListener("keydown", function (e) { if (e.key === "Escape") close(); });

        view.start();
    }

    document.addEventListener("DOMContentLoaded", function () {
        var map = document.getElementById("world-map");
        if (map) start(map);
    });
})();
