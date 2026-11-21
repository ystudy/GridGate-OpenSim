# Account pages: big headline left, form right

A small change on the public side. It follows the public look (`2026-11-17-public-look-design.md`) and the owner's
GOALS.md item "Reset password - restyle ours: big headline left, email box right, 'Back to login' link", widened by the
owner to every account page.

## Why

Today the account pages are a small centred card on an empty navy page. The owner wants them to look like the rest of
the new public side: bold and confident, with the page's name as a big headline. Only the look changes; what each page
asks and does stays the same.

## Pages

- Sign in (`/guest/sign-in`)
- Sign-in code (`/guest/sign-in/code`)
- Forgot password (`/guest/forgot-password`, the menu's "Reset password")
- Choose a new password (`/guest/reset-password`, from the emailed link)
- Sign up (`/signup`)

Not changed (owner's choice or out of scope): the resident's two-factor setup page (`/guest/two-factor`), the staff
`/login` pages (staff look), and the message-only pages that also use the small card (sign-up confirm, email confirm,
support's confirm, lost-link and ticket pages).

## Approaches

- **A. One shared CSS layout, each page's markup changed by hand (chosen by the owner).** A new `account-split` style
  in `guest.css`; each page puts its headline and line in a left column and its card on the right. Each page's whole
  layout stays readable in its own file, and pages whose words change with the state keep doing it as they do now.
- B. A shared "account page" frame the pages pass their headline to. Less repeated markup, but the headline is split
  from the page that shows it, and the reset page's line changes by state, so state would have to be passed through.
  More fiddly than the repetition it saves.

## The layout

```
 wide (768px and up)                                     phone
 ┌───────────────────────────┬──────────────────────┐    ┌──────────────────┐
 │ FORGOT                    │ ┌──────────────────┐ │    │ FORGOT PASSWORD  │
 │ PASSWORD                  │ │ Avatar name [__] │ │    │ Enter your ...   │
 │ Enter your avatar name    │ │ Email       [__] │ │    │ ┌──────────────┐ │
 │ and email ...             │ │ [  SEND LINK  ]  │ │    │ │ form         │ │
 │                           │ │ Back to sign in  │ │    │ └──────────────┘ │
 │                           │ └──────────────────┘ │    └──────────────────┘
 └───────────────────────────┴──────────────────────┘
```

- **Left:** the page's `<h1>`, bigger than other public pages' (about the front page headline's size, left-aligned,
  vertically centred beside the card), then the page's existing intro line under it.
- **Right:** the glowing card holding the form, the error box, the state messages (sent, expired, done, closed) and
  the links under the form.
- **Phone (under 768px):** one column, headline and line first, then the card; no sideways scroll at 375px.
- Sign up's card is wider than the others, so step 2's starter avatars get room.

## Calls made here (the owner can correct any)

- The headlines are the pages' existing titles, and the left line is each page's existing intro text, so no new words
  need translating. When a page is showing a state message instead of its form (link sent, link expired, password
  changed, sign-up closed or waiting), the left side shows the headline alone and the message sits in the card.
- The choose-a-new-password form gains a "Back to sign in" link under it (existing words), like the forgot-password page.
- No photo or picture behind these pages: plain navy, as now. The card keeps its usual glow.
- `sign-in-card` in `site.css` stays for the pages not changed here.

## Tests

- Each of the five pages renders the split layout with its headline in the left column and its form in the card.
- Forgot password and choose a new password both show "Back to sign in".
- Every state of each page (sent, expired, done, closed, waiting, resend, code unknown) still shows its message.
- The existing guest page tests still pass. Checked in a browser at desktop width and at 375px.
