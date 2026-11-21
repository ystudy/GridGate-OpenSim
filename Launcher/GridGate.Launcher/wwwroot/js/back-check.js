// On signed-in pages only. A browser may keep the whole page in its back/forward cache and show it again on Back without
// asking the server, whatever the page's "no-store" says (Safari and Firefox do). Then load it again: a visitor who has
// signed out meanwhile gets the sign-in page, not the page they saw while signed in.
window.addEventListener('pageshow', function (e) {
    if (e.persisted) location.reload();
});
