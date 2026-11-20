// The Regions page map (spec 5.2): the map itself is map-core.js (tiles, outlines, pan and zoom); this file adds what only
// staff do with it: a region's state on its outline, the region panel, and placing a new region by dragging or clicking a
// type. Hand-written, no library; needs live.js and map-core.js.

// getJson(url) and getText(url) fetch from the launcher. A 401 (the sign-in expired) goes to the sign-in page.
function mapFetch(url, accept) {
    return fetch(url, { headers: { Accept: accept }, credentials: "same-origin" }).then(function (r) {
        if (r.status === 401) {
            signInAgain();
            throw new Error("signed out");
        }
        return r;
    });
}

function getJson(url) {
    return mapFetch(url, "application/json").then(function (r) {
        // The placement API explains a refused spot in a 400's body.
        var type = r.headers.get("Content-Type") || "";
        if (type.indexOf("json") < 0) throw new Error("HTTP " + r.status);
        return r.json();
    });
}

function getText(url) {
    return mapFetch(url, "text/html").then(function (r) {
        if (!r.ok) throw new Error("HTTP " + r.status);
        return r.text();
    });
}

function startMap(map) {
    var POLL_MS = 5000, ASK_GAP_MS = 250, CLICK_SLOP_PX = 5;

    var placementUrl = map.getAttribute("data-placement");
    var addUrl = map.getAttribute("data-add");
    var panelUrl = map.getAttribute("data-panel");
    // Moving a region (?move=<id>): its spot is chosen on the map and sent to its move page.
    var moveId = map.getAttribute("data-move");
    var moveName = map.getAttribute("data-move-name");
    var moveSize = parseInt(map.getAttribute("data-move-size"), 10) || 1;

    var placementText = document.getElementById("placement-text");
    var backButton = document.getElementById("back-to-mine");
    var panel = document.getElementById("region-panel");
    var panelBody = panel.querySelector(".region-panel-body");
    // The panel's HTML is fetched, so confirm.js never saw its forms: a form with data-confirm asks first here too.
    panelBody.addEventListener("submit", function (e) {
        var question = e.target.getAttribute && e.target.getAttribute("data-confirm");
        if (question && !window.confirm(question)) e.preventDefault();
    });
    var panelColumn = document.getElementById("panel-column");
    var typeItems = document.querySelectorAll("#region-types li[data-type]");
    var selectedId = map.getAttribute("data-selected") || null;

    // Placing: the selected type, the square under the pointer and the placement answers so far.
    var placingType = null;
    var ghost = document.createElement("div");
    ghost.className = "map-ghost";
    ghost.hidden = true;
    var ghostSquare = null;
    var answers = {};      // "x,y" -> { ok, text }; cleared when the regions change
    var lastAsk = 0, askTimer = null;

    var mapView = startMapView(map, {
        tilesUrl: map.getAttribute("data-tiles"),
        regionsUrl: map.getAttribute("data-regions"),
        pollMs: POLL_MS,
        load: getJson,
        regionsOf: function (json) { return json.regions; },
        keyOf: function (r) { return r.id; },
        markerClass: function (r) { return "state-" + r.state + (r.reserved ? " reserved" : "") + (r.id === selectedId ? " selected" : ""); },
        markerTitle: function (r) { return r.name + ": " + r.stateText; },
        zoomIn: document.getElementById("zoom-in"),
        zoomOut: document.getElementById("zoom-out"),
        fitFirst: fitFirst,
        onRegions: function (list) {
            answers = {};
            backButton.hidden = !list.some(function (r) { return !r.reserved; });
        },
        onRelayout: function () { if (ghostSquare) mapView.place(ghost, ghostSquare.x, ghostSquare.y, ghostSize()); },
        onHover: function (p) { if (placingType) moveGhost(p); },
        onLeave: function () { ghost.hidden = true; },
        onClick: function (p, region) {
            if (placingType) chooseSpot(p);
            else if (region) openPanel(region.id);
        }
    });
    mapView.layer.appendChild(ghost);

    // "Back to my regions": every live region (not the reserved spots of deleted ones).
    function fitMine() {
        return mapView.fitAll(mapView.regions().filter(function (r) { return !r.reserved; }));
    }

    // The first view: the selected region, else every live region.
    function fitFirst() {
        var chosen = selectedId && mapView.regionByKey(selectedId);
        if (chosen) mapView.fitBox(chosen.x, chosen.y, chosen.x + chosen.size, chosen.y + chosen.size);
        else fitMine();
    }

    // The region panel: a column from lg up, an offcanvas over the map on phones.
    function setRegionInUrl(id) {
        var url = new URL(window.location.href);
        if (id) url.searchParams.set("region", id); else url.searchParams.delete("region");
        history.replaceState(null, "", url.pathname + url.search + url.hash);
    }

    function markSelected() {
        mapView.restyle();
        document.querySelectorAll("#region-list tr[data-region]").forEach(function (tr) {
            tr.classList.toggle("table-active", tr.getAttribute("data-region") === selectedId);
        });
    }

    function isOverlay() { return window.getComputedStyle(panel).position === "fixed"; }

    function openPanel(id) {
        selectedId = id;
        setRegionInUrl(id);
        markSelected();
        panelColumn.hidden = false;
        panelBody.textContent = "Loading…";
        if (isOverlay() && window.bootstrap) bootstrap.Offcanvas.getOrCreateInstance(panel).show();
        getText(panelUrl + encodeURIComponent(id) + "?partial=1").then(function (html) {
            if (selectedId === id) panelBody.innerHTML = html;
        }, function () {
            if (selectedId === id) panelBody.textContent = "Couldn't load this region.";
        });
        mapView.update();
    }

    function closePanel() {
        if (!selectedId) return;
        selectedId = null;
        setRegionInUrl(null);
        markSelected();
        if (panel.classList.contains("show") && window.bootstrap) bootstrap.Offcanvas.getOrCreateInstance(panel).hide();
        panelColumn.hidden = true;
        panelBody.textContent = "";
        mapView.update();
    }

    // Placing a new region.
    function selectType(id) {
        if (moveId) return; // the spot is for the region being moved
        placingType = id;
        typeItems.forEach(function (li) {
            var on = li.getAttribute("data-type") === id;
            li.classList.toggle("selected", on);
            li.setAttribute("aria-pressed", on ? "true" : "false");
        });
        if (id) {
            map.classList.add("placing");
            showPlacement("Now choose a spot on the map.", true);
        } else {
            map.classList.remove("placing");
            hideGhost();
            showPlacement("", true);
        }
    }

    function showPlacement(text, ok) {
        placementText.textContent = text;
        placementText.classList.toggle("text-danger", !ok);
        ghost.classList.toggle("bad", !ok);
    }

    // The squares along the ghost's side: the moving region's size, else a single square.
    function ghostSize() { return moveId ? moveSize : 1; }

    function hideGhost() { ghost.hidden = true; ghostSquare = null; }

    function squareAt(p) {
        var w = mapView.toWorld(p.x, p.y);
        return { x: Math.floor(w.x), y: Math.floor(w.y) };
    }

    // The placement API's answer ({ ok, text }) for a square, asked once until the regions change. Every request
    // keeps ASK_GAP_MS after the one before (at most 4 a second): a click right after a hover waits its turn.
    function ask(sq) {
        var key = sq.x + "," + sq.y;
        if (!answers[key]) {
            var wait = Math.max(0, lastAsk + ASK_GAP_MS - Date.now());
            lastAsk = Date.now() + wait;
            answers[key] = new Promise(function (resolve) { setTimeout(resolve, wait); }).then(function () {
                // A moving region may overlap its own old square.
                var query = "?x=" + sq.x + "&y=" + sq.y + "&size=" + ghostSize() + (moveId ? "&ignore=" + encodeURIComponent(moveId) : "");
                return getJson(placementUrl + query);
            }).then(function (json) {
                return { ok: json.ok === true, text: json.text };
            }, function (e) {
                delete answers[key];
                throw e;
            });
        }
        return answers[key];
    }

    function sameSquare(a, b) { return a && b && a.x === b.x && a.y === b.y; }

    function showAnswer(sq, answer) {
        if (sameSquare(sq, ghostSquare)) showPlacement(answer.text, answer.ok);
    }

    // Asks about the ghost's square at most 4 times a second; only the latest square is asked about.
    function askSoon() {
        if (askTimer) return;
        askTimer = setTimeout(function () {
            askTimer = null;
            var sq = ghostSquare;
            if (!sq) return;
            ask(sq).then(function (a) { showAnswer(sq, a); }, function () { /* the next move asks again */ });
        }, Math.max(0, lastAsk + ASK_GAP_MS - Date.now()));
    }

    function moveGhost(p) {
        var sq = squareAt(p);
        ghost.hidden = false;
        if (sameSquare(sq, ghostSquare)) return;
        ghostSquare = sq;
        mapView.place(ghost, sq.x, sq.y, ghostSize());
        if (answers[sq.x + "," + sq.y]) ask(sq).then(function (a) { showAnswer(sq, a); }, function () { });
        else askSoon();
    }

    // A drop or a click on a spot: goes on when the spot is ok, else the reason stays shown.
    function chooseSpot(p) {
        moveGhost(p);
        var sq = ghostSquare;
        ask(sq).then(function (a) {
            showAnswer(sq, a);
            if (a.ok && sameSquare(sq, ghostSquare)) spotChosen(sq.x, sq.y);
        }, function () { /* a 401 is already handled; otherwise the person can click again */ });
    }

    // Where an ok spot leads.
    function spotChosen(x, y) {
        if (moveId) {
            window.location.href = panelUrl + encodeURIComponent(moveId) + "/move?x=" + x + "&y=" + y;
            return;
        }
        window.location.href = addUrl + "?type=" + encodeURIComponent(placingType) + "&x=" + x + "&y=" + y;
    }

    backButton.addEventListener("click", fitMine);

    // Types: a click selects one (again unselects it); a drag carries it onto the map.
    typeItems.forEach(function (li) {
        var id = li.getAttribute("data-type");
        var drag = null;
        li.tabIndex = 0;
        li.setAttribute("role", "button");
        li.setAttribute("aria-pressed", "false");
        li.addEventListener("dragstart", function (e) { e.preventDefault(); });
        li.addEventListener("keydown", function (e) {
            if (e.key === "Enter" || e.key === " ") { e.preventDefault(); selectType(placingType === id ? null : id); }
        });
        li.addEventListener("pointerdown", function (e) {
            if (e.pointerType === "mouse" && e.button !== 0) return;
            drag = { id: e.pointerId, x: e.clientX, y: e.clientY, moved: false };
            li.setPointerCapture(e.pointerId);
            if (e.pointerType === "mouse") e.preventDefault();
        });
        li.addEventListener("pointermove", function (e) {
            if (!drag || drag.id !== e.pointerId) return;
            if (!drag.moved && Math.abs(e.clientX - drag.x) + Math.abs(e.clientY - drag.y) > CLICK_SLOP_PX) {
                drag.moved = true;
                selectType(id);
            }
            if (!drag.moved) return;
            var p = mapView.pointIn(e);
            if (p.inside) moveGhost(p); else ghost.hidden = true;
        });
        li.addEventListener("pointerup", function (e) {
            if (!drag || drag.id !== e.pointerId) return;
            var moved = drag.moved;
            drag = null;
            if (!moved) { selectType(placingType === id ? null : id); return; }
            var p = mapView.pointIn(e);
            if (p.inside) chooseSpot(p);
        });
        li.addEventListener("pointercancel", function () { drag = null; });
    });

    // Escape unselects the type, then closes the panel.
    document.addEventListener("keydown", function (e) {
        if (e.key !== "Escape") return;
        if (placingType && !moveId) selectType(null);
        else closePanel();
    });
    document.getElementById("close-panel").addEventListener("click", closePanel);
    // The phone header's close button (and Bootstrap's own Escape) hide the offcanvas.
    panel.addEventListener("hidden.bs.offcanvas", closePanel);

    if (moveId) {
        // Only the spot is asked for: the region types step aside and the map is ready to click.
        document.getElementById("types-column").hidden = true;
        placingType = moveId;
        map.classList.add("placing");
        showPlacement("Choose a new spot for " + moveName + ".", true);
    }

    if (selectedId) openPanel(selectedId);
    mapView.start();
}

document.addEventListener("DOMContentLoaded", function () {
    var map = document.getElementById("map");
    if (map) startMap(map);
});
