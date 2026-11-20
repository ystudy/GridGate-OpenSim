// The map's drawing part, shared by the staff Regions page (map.js) and the world map (world-map.js): Robust's tiles through
// the launcher, an outline with its name for every region, drag to pan, the wheel and two buttons to zoom, and a poll for the
// region list. Hand-written, no library. What a click or a hover means is the page's own (options below).
//
// Positions are region coordinates: one per 256 m square, Y grows north, so screen y runs the other way.
// A Robust zoom z tile covers 2^(z-1) squares per side and is drawn 256 pixels wide, so zoom 1 is the closest.
// A region is { x, y } (its south-west square) and its size: w and h squares, or size for a square one.
//
// options:
//   tilesUrl, regionsUrl, pollMs   where the tiles and the region list come from, and how often the list is asked again
//   load(url) -> Promise<json>     fetches the region list (the page handles a sign-in that ended)
//   regionsOf(json) -> array       the regions in what load gave
//   keyOf(r) -> string             a region's key, the same on every poll
//   markerClass(r) -> string       classes after "map-marker" (a state, "selected", ...)
//   markerTitle(r) -> string       the outline's tooltip
//   zoomIn, zoomOut                the two zoom buttons
//   startZoom                      the zoom before the first fit (3 when absent)
//   fitFirst()                     the first view, once the list is there and the map has a size; returning false
//                                  (nothing to fit yet) asks again when the list next changes
//   onClick(p, region)             a press that didn't move: p in pixels from the map's corner, region the one under it or null
//   onHover(p), onLeave()          a mouse or pen over the map (not while panning), and leaving it
//   onRegions(list)                after the list changed (not when a poll brings the same list)
//   onRelayout()                   after a zoom, for elements the page placed in the layer itself
function startMapView(map, options) {
    var MIN_ZOOM = 1, MAX_ZOOM = 8, TILE_PX = 256, WORLD = 65536;
    var MISSING_RETRY_MS = 60000, CLICK_SLOP_PX = 5;
    function noop() { }
    var onClick = options.onClick || noop, onHover = options.onHover || noop, onLeave = options.onLeave || noop;
    var onRegions = options.onRegions || noop, onRelayout = options.onRelayout || noop, fitFirstView = options.fitFirst || noop;
    var markerClass = options.markerClass || function () { return ""; };
    var markerTitle = options.markerTitle || function (r) { return r.name; };
    var keyOf = options.keyOf;
    var zoomIn = options.zoomIn, zoomOut = options.zoomOut;

    // The view: the square at the map's centre and the zoom. Elements in the layer are placed relative to an
    // origin square, reset on each zoom, so pixel numbers stay small; panning only moves the layer's transform.
    var view = { x: 1000.5, y: 1000.5, zoom: options.startZoom || 3 };
    var origin = { x: 1000, y: 1000 };
    var layer = document.createElement("div");
    layer.className = "map-layer";
    map.appendChild(layer);

    var tiles = {};        // "z/x/y" -> <img> in the layer
    var tileOk = {};       // "z/x/y" -> true once its picture loaded
    var tileMissing = {};  // "z/x/y" -> when it failed; tried again after MISSING_RETRY_MS
    var tileVersion = 0;   // bumped when the regions change, so changed tiles skip the browser's cache
    var regions = [];      // the latest region list
    var regionsJson = "";
    var markers = {};      // region key -> marker element
    var fitted = false;

    function sizeW(r) { return r.w || r.size || 1; }
    function sizeH(r) { return r.h || r.size || 1; }
    function scale() { return TILE_PX / Math.pow(2, view.zoom - 1); }  // pixels per square
    function span(zoom) { return Math.pow(2, zoom - 1); }               // squares per tile side
    function width() { return map.clientWidth; }
    function height() { return map.clientHeight; }
    function clamp(v, lo, hi) { return Math.max(lo, Math.min(hi, v)); }

    // The square (fractional) under a point given in pixels from the map's top-left corner.
    function toWorld(px, py) {
        var s = scale();
        return { x: view.x + (px - width() / 2) / s, y: view.y - (py - height() / 2) / s };
    }

    function pointIn(e) {
        var r = map.getBoundingClientRect();
        return { x: e.clientX - r.left, y: e.clientY - r.top, inside: e.clientX >= r.left && e.clientX < r.right && e.clientY >= r.top && e.clientY < r.bottom };
    }

    // Places el over the squares from (x, y) to (x + w, y + h), y being the south edge; h is w when absent.
    function place(el, x, y, w, h) {
        if (h === undefined) h = w;
        var s = scale();
        el.style.left = ((x - origin.x) * s) + "px";
        el.style.top = (-(y + h - origin.y) * s) + "px";
        el.style.width = (w * s) + "px";
        el.style.height = (h * s) + "px";
    }

    function moveLayer() {
        var s = scale();
        var tx = width() / 2 - (view.x - origin.x) * s;
        var ty = height() / 2 + (view.y - origin.y) * s;
        layer.style.transform = "translate(" + Math.round(tx) + "px," + Math.round(ty) + "px)";
    }

    // Redraws after a zoom: a new origin, every element placed again, the old zoom's tiles dropped.
    function relayout() {
        origin = { x: Math.floor(view.x), y: Math.floor(view.y) };
        for (var key in tiles) { tiles[key].remove(); }
        tiles = {};
        for (var i = 0; i < regions.length; i++) {
            var r = regions[i], el = markers[keyOf(r)];
            if (el) place(el, r.x, r.y, sizeW(r), sizeH(r));
        }
        onRelayout();
        update();
    }

    // After any move: the layer's position, the tiles in view, the zoom buttons.
    function update() {
        view.x = clamp(view.x, 0, WORLD);
        view.y = clamp(view.y, 0, WORLD);
        moveLayer();
        loadTiles();
        zoomIn.disabled = view.zoom <= MIN_ZOOM;
        zoomOut.disabled = view.zoom >= MAX_ZOOM;
    }

    function loadTiles() {
        var n = span(view.zoom), z = view.zoom;
        var a = toWorld(0, height()), b = toWorld(width(), 0);
        var x0 = clamp(Math.floor(a.x / n) * n, 0, WORLD - n), x1 = clamp(Math.floor(b.x / n) * n, 0, WORLD - n);
        var y0 = clamp(Math.floor(a.y / n) * n, 0, WORLD - n), y1 = clamp(Math.floor(b.y / n) * n, 0, WORLD - n);
        var wanted = {};
        for (var tx = x0; tx <= x1; tx += n) {
            for (var ty = y0; ty <= y1; ty += n) {
                var key = z + "/" + tx + "/" + ty;
                wanted[key] = true;
                if (tiles[key] || (tileMissing[key] && Date.now() - tileMissing[key] < MISSING_RETRY_MS)) continue;
                tiles[key] = makeTile(key, tx, ty, n);
            }
        }
        // Tiles well out of view go, so a long pan does not pile up pictures.
        for (var k in tiles) {
            if (!wanted[k]) { tiles[k].remove(); delete tiles[k]; }
        }
    }

    // The tile links ignore the query string; ?v= only gets past the browser's cache after a change.
    function tileSrc(key) { return options.tilesUrl + "/" + key + (tileVersion ? "?v=" + tileVersion : ""); }

    // After the regions change: the tiles in view under a changed region are fetched again, and tiles that had no
    // picture are tried again, since a region that came up or went may have redrawn them. Tiles out of view wait.
    function refreshTiles(changed) {
        tileVersion++;
        tileMissing = {};
        var n = span(view.zoom);
        changed.forEach(function (r) {
            for (var tx = Math.floor(r.x / n) * n; tx < r.x + sizeW(r); tx += n) {
                for (var ty = Math.floor(r.y / n) * n; ty < r.y + sizeH(r); ty += n) {
                    var img = tiles[view.zoom + "/" + tx + "/" + ty];
                    if (img) img.src = tileSrc(view.zoom + "/" + tx + "/" + ty);
                }
            }
        });
        loadTiles();
    }

    function makeTile(key, tx, ty, n) {
        var img = document.createElement("img");
        img.className = "map-tile";
        img.alt = "";
        img.draggable = false;
        place(img, tx, ty, n);
        // No picture leaves the water colour showing; the markers there get stripes.
        img.onload = function () { tileOk[key] = true; delete tileMissing[key]; markTiles(); };
        img.onerror = function () {
            img.remove();
            if (tiles[key] === img) delete tiles[key];
            delete tileOk[key];
            tileMissing[key] = Date.now();
            markTiles();
        };
        img.src = tileSrc(key);
        layer.insertBefore(img, layer.firstChild);
        return img;
    }

    // A region with no tile picture under its south-west square is striped.
    function markTiles() {
        var n = span(view.zoom);
        for (var i = 0; i < regions.length; i++) {
            var r = regions[i], el = markers[keyOf(r)];
            if (!el) continue;
            var key = view.zoom + "/" + Math.floor(r.x / n) * n + "/" + Math.floor(r.y / n) * n;
            el.classList.toggle("no-tile", !tileOk[key]);
        }
    }

    // Zooms to a level, keeping the square under the point (map pixels; the centre when absent) where it is.
    function zoomTo(zoom, px, py) {
        zoom = clamp(zoom, MIN_ZOOM, MAX_ZOOM);
        if (zoom === view.zoom) return;
        if (px === undefined) { px = width() / 2; py = height() / 2; }
        var w = toWorld(px, py);
        view.zoom = zoom;
        var s = scale();
        view.x = w.x - (px - width() / 2) / s;
        view.y = w.y + (py - height() / 2) / s;
        relayout();
        markTiles();
    }

    // Shows the box from (x0, y0) to (x1, y1) with a square to spare on each side, as close as fits.
    function fitBox(x0, y0, x1, y1) {
        var zoom = MAX_ZOOM;
        for (var z = MIN_ZOOM; z <= MAX_ZOOM; z++) {
            var s = TILE_PX / span(z);
            if ((x1 - x0 + 2) * s <= width() && (y1 - y0 + 2) * s <= height()) { zoom = z; break; }
        }
        view.zoom = zoom;
        view.x = (x0 + x1) / 2;
        view.y = (y0 + y1) / 2;
        relayout();
        markTiles();
    }

    // Shows every region of the list; false when there is none or the map has no size yet.
    function fitAll(list) {
        if (list.length === 0 || width() === 0) return false;
        var x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity;
        list.forEach(function (r) {
            x0 = Math.min(x0, r.x); y0 = Math.min(y0, r.y);
            x1 = Math.max(x1, r.x + sizeW(r)); y1 = Math.max(y1, r.y + sizeH(r));
        });
        fitBox(x0, y0, x1, y1);
        return true;
    }

    function styleMarker(r) {
        var el = markers[keyOf(r)], extra = markerClass(r);
        el.className = "map-marker" + (extra ? " " + extra : "");
        el.title = markerTitle(r);
    }

    // Applies markerClass and markerTitle again, after something the page shows in them changed.
    function restyle() {
        regions.forEach(styleMarker);
        markTiles();
    }

    // Markers: one per region, its name always on top.
    function showRegions(list) {
        // What changed since the last poll (place, size or state; added or gone), for the tiles. Not on the first.
        var before = {}, changed = [];
        regions.forEach(function (r) { before[keyOf(r)] = r; });
        list.forEach(function (r) {
            var old = before[keyOf(r)];
            if (!old || old.state !== r.state || old.x !== r.x || old.y !== r.y || sizeW(old) !== sizeW(r) || sizeH(old) !== sizeH(r)) changed.push(r);
            delete before[keyOf(r)];
        });
        for (var gone in before) changed.push(before[gone]);
        var first = regionsJson === "";
        regions = list;
        var seen = {};
        list.forEach(function (r) {
            var key = keyOf(r);
            seen[key] = true;
            var el = markers[key];
            if (!el) {
                el = document.createElement("div");
                el.setAttribute("data-region", key);
                var name = document.createElement("span");
                name.className = "map-marker-name";
                el.appendChild(name);
                layer.appendChild(el);
                markers[key] = el;
            }
            styleMarker(r);
            el.firstChild.textContent = r.name;
            place(el, r.x, r.y, sizeW(r), sizeH(r));
        });
        for (var key in markers) {
            if (!seen[key]) { markers[key].remove(); delete markers[key]; }
        }
        markTiles();
        if (!first && changed.length > 0) refreshTiles(changed);
    }

    function refresh() {
        return options.load(options.regionsUrl).then(function (json) {
            var list = options.regionsOf(json);
            var text = JSON.stringify(list);
            if (text === regionsJson) return;
            showRegions(list);
            regionsJson = text;
            onRegions(list);
            fitFirst();
        });
    }

    // The first view waits until the list is there and the map has a size, and until the page had something to fit:
    // a list that was empty when the page opened (the database down, no region running) mustn't use it up.
    function fitFirst() {
        if (fitted || !regionsJson || width() === 0) return;
        fitted = fitFirstView() !== false;
    }

    function resized() {
        fitFirst();
        update();
    }

    function every(ms, run) {
        function tick() {
            run().catch(function () { /* the launcher may be restarting; try again next time */ })
                .then(function () { setTimeout(tick, ms); });
        }
        tick();
    }

    function regionByKey(key) {
        for (var i = 0; i < regions.length; i++) { if (keyOf(regions[i]) === key) return regions[i]; }
        return null;
    }

    // Dragging the map pans it; a press that does not move is a click.
    var press = null;
    map.addEventListener("pointerdown", function (e) {
        if (press || (e.pointerType === "mouse" && e.button !== 0)) return;
        press = { id: e.pointerId, x: e.clientX, y: e.clientY, viewX: view.x, viewY: view.y, moved: false };
        map.setPointerCapture(e.pointerId);
        e.preventDefault();
    });
    map.addEventListener("pointermove", function (e) {
        if (press && press.id === e.pointerId) {
            var dx = e.clientX - press.x, dy = e.clientY - press.y;
            if (!press.moved && Math.abs(dx) + Math.abs(dy) > CLICK_SLOP_PX) {
                press.moved = true;
                map.classList.add("panning");
            }
            if (press.moved) {
                var s = scale();
                view.x = press.viewX - dx / s;
                view.y = press.viewY + dy / s;
                update();
            }
        }
        if (e.pointerType !== "touch" && !(press && press.moved)) onHover(pointIn(e));
    });
    map.addEventListener("pointerup", function (e) {
        if (!press || press.id !== e.pointerId) return;
        var clicked = !press.moved;
        press = null;
        map.classList.remove("panning");
        if (!clicked) return;
        var hit = document.elementFromPoint(e.clientX, e.clientY);
        var marker = hit && hit.closest ? hit.closest(".map-marker") : null;
        onClick(pointIn(e), marker ? regionByKey(marker.getAttribute("data-region")) : null);
    });
    map.addEventListener("pointercancel", function () { press = null; map.classList.remove("panning"); });
    map.addEventListener("pointerleave", function (e) { if (e.pointerType !== "touch" && !press) onLeave(); });

    // The wheel zooms one level about the pointer; a burst from a touchpad counts once.
    var lastWheel = 0;
    map.addEventListener("wheel", function (e) {
        e.preventDefault();
        if (e.deltaY === 0 || Date.now() - lastWheel < 150) return;
        lastWheel = Date.now();
        var p = pointIn(e);
        zoomTo(view.zoom + (e.deltaY < 0 ? -1 : 1), p.x, p.y);
    }, { passive: false });

    zoomIn.addEventListener("click", function () { zoomTo(view.zoom - 1); });
    zoomOut.addEventListener("click", function () { zoomTo(view.zoom + 1); });
    zoomIn.hidden = false;
    zoomOut.hidden = false;

    if (window.ResizeObserver) new ResizeObserver(resized).observe(map);
    else window.addEventListener("resize", resized);

    relayout();

    return {
        layer: layer,
        place: place,
        toWorld: toWorld,
        pointIn: pointIn,
        fitBox: fitBox,
        fitAll: fitAll,
        update: update,
        restyle: restyle,
        regions: function () { return regions; },
        regionByKey: regionByKey,
        // Starts asking for the region list, now and every pollMs.
        start: function () { every(options.pollMs, refresh); }
    };
}
