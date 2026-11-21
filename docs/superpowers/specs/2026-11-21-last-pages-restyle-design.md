# The last public pages in the new look

A small change on the public side. It follows the public look (`2026-11-17-public-look-design.md`) and the owner's
GOALS.md item "Restyle the ones we already have: sign in, sign up, legal pages, help/support, how to connect". Sign in
and sign up were done with the account pages (`2026-11-20-account-pages-design.md`); this does the rest. The owner
picked this over Market on 2026-11-21. Every call below is mine unless marked.

## Why

These pages already sit on the dark navy page with the top bar and footer, but inside they are still the old staff
look: grey panels, a small centred card, plain headings. Next to the front page, Featured, Residents and the Avatar
page they look unfinished. Only the look changes; what each page says, asks and does stays the same.

## Pages

- The legal pages: `/terms`, `/privacy`, `/cookies`, `/contact`.
- Accepting the terms: `/guest/terms`.
- Help: `/support`, a ticket (`/support/ticket`), confirming a ticket (`/support/confirm`), a lost ticket link
  (`/support/lost-link`).
- How to connect: `/guest/connect` (the Avatar page's Download tab).
- The two message-only sign-up pages the account pages left on the small card: `/guest/confirm` and
  `/guest/confirm-email`.

Not changed: the staff `/login` pages (staff look), the resident's two-factor page (owner's choice in the account
pages design), and the resident's upload, coins and regions pages (not on the list).

## Approaches

- **A. Reuse the styles the newer pages already have, plus a few small new ones (chosen).** The page head of Featured
  and Residents, the account pages' split, the glowing cards. Each page's markup is changed by hand.
- B. A shared "public page" frame with slots for title, line and cards. Less repeated markup, but the pages differ a lot
  (a long text, a form, a list of messages, download cards), so the frame would be mostly options.
- C. CSS only, restyling `.panel` and friends under `.guest-site`. No markup change, but the headline and line can't
  move into the split, and the staff pages' class names would carry public-side meaning.

## How each page looks

- **Legal pages:** the page head (big title, then "Version N, saved D" as the line under it). The note about the
  language it is shown in stays above the text. The operator's text sits in one card at a reading width (about 48rem),
  with headings inside it in normal case so long legal headings stay easy to read.
- **Accepting the terms:** the page head ("Terms of service", line "Grid name · version N"), the text in the same card,
  then a second card with the accept button (big, yellow, uppercase) or, once accepted, the "accepted" words and a
  Continue button. The "terms changed while you were reading" warning stays above the text.
- **Help (`/support`):** the account pages' wide split. Left: the headline, the intro line, the resident's open tickets
  and the lost-link link. Right: the form in a card. After sending, the card says to check the email.
- **A ticket:** a back link, the subject as the headline, the number, category and state as the line under it. Each
  message is a card; staff messages have a blue edge and glow. The reply form is a card.
- **Confirm a ticket, lost link, sign-up confirm, email confirm, and a ticket link that ran out:** the account split
  (headline left, card right), like Forgot password.
- **How to connect:** the page head (heading and line). The two viewer choices are glowing cards; Firestorm's Download
  is a yellow button. The grid settings, steps and help are cards too, with the login address kept large in the steps.

All the ids and texts tests rely on stay. No new words, so no new translations.

## Left to the builder

The exact spacing, and whether a style is shared or per page.
