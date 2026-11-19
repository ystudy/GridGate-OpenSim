| **A server sent handoffs for people it hadn't placed verified.** Normal for people who walked in from a stock region, came in through the Gatekeeper, or were placed without a verified start. Counted on one row per server. | Notice | Nothing, usually. If it keeps rising, check the region is running and its GridGate is up to date. |
| **A server reported arrivals in regions it doesn't cover.** A region that is new, or moved, covers nothing until you confirm it or approve the move. Counted on one row per server. | Notice | Confirm the region, or approve its move, under Regions. |
# Money verification: a guide for owners

GridGate's money works only where it can check two things: **which Region Server or region program runs a region**, and
**where each person really is**. This page explains what the Money page (`/money`) shows about that, why money can be off in
a region or for one person, and what you can do about it. It is for the grid owner and staff; you don't need to know how
OpenSim works inside.

Everything here is about money only. A region or a person without money can still do everything else: visit, chat, build,
run scripts and teleport.

## What "verified" means

A region is **verified** when all of these are true:

1. **It belongs to a known key.** Regions on this computer (the Grid Asset Server) belong to their region program. A
   paired Region Server's regions belong to that server: the first time the grid sees a region on a paired server, it is
   pinned to that server, and it stays with it.
2. **Its program runs GridGate's money module, new enough to say where people go** when they teleport or walk out of the
   region.
3. **It checked in recently.** Each region program checks in with the money service when a region starts and every 3
   minutes after. A region counts as verified for 10 minutes after its last check-in.

A person can pay, buy and be charged only while **both** are true:

- the region they're in is verified, and
- the money service itself knows they are there: they logged in there, came in through this grid's hypergrid gate into one
  of this computer's regions, or a verified region handed them on when they teleported or crossed a border. A region saying
  "this person is here" isn't enough on its own.

Receiving coins needs no such check: a vendor's owner or a tip jar's owner is often offline, and being paid can't cost
anyone anything. Script payouts need a verified region, plus the object owner's own "My scripts may pay out" switch and the
payout limits.

The **Regions** list on the Money page shows every region on the grid with a ✔ or a ✖. Each ✖ has one of the reasons below,
and a **How to fix** link to that reason here. The **Region Servers** list shows each paired server's own status and its
GridGate money version (2 or later is needed).

When something is missing or unclear, money is refused, never guessed. The reasons below are how you find out why.

### Not paired

The region runs on a computer that isn't paired with this grid's money, so money is off there.

**What to do:** pair it. On this Money page, under **Region Servers**, make a pairing code, and type it into that Region
Server's Money page. Its regions are verified once their programs check in, within a few minutes.

Until a Region Server is paired, uploads that cost a fee (textures, sounds, animations and meshes) are refused there, and
the uploader is told that the server isn't paired with the money service yet. Nothing is charged.

A pairing code works once, for 15 minutes. Five wrong tries from one address block that address until the code ends, and the
code still works from other addresses; only you can cancel it, with **Cancel the code** below. Only computers on this
computer's own network (or this computer itself) can reach the money port at all.

If that computer runs stock OpenSim rather than GridGate, it can't be paired; its regions stay open to visitors, without
money.

### Moved, waiting for you

The region belongs to one Region Server, but the grid now lists it on a different one. GridGate never moves a region to
another server's key without you, so money is off there until you decide. The list says which server it came from.

This is normal when you really moved the region, for example to a new computer. If you didn't, find out who did before you
approve it.

**What to do (owner only):**

- **Approve move:** the region now belongs to the server it registers from. This is written to the audit log.
- **Keep off:** the region stays with the server it belongs to. Money comes back by itself if that server registers it
  again. This is written to the audit log too, and clears the alert.

A few things that work the same way:

- **A brand-new region** on a paired server is pinned to it as waiting, like the regions listed when it paired: money is off
  there until you confirm it (see [Waiting for you to confirm](#waiting-for-you-to-confirm)).
- **Pairing a server again:** if it is the same computer, its regions stay with it. If a different computer takes over the
  same address and ports, its regions show as moved until you approve them.
- **Removing a paired server** ends its key at once, and its regions show as not paired. Its pins stay, so a region of it
  that another server lists later shows as moved until you decide.

### Waiting for you to confirm

When a Region Server pairs, the regions the grid already lists at its address and ports are pinned to it, but they wait.
A region that first appears at its address and ports later waits the same way. Neither pairing nor a new region is handed
over by itself, so money is off in them until you confirm them. The Regions list shows when each one first appeared.

**What to do (owner only):** under **Region Servers**, press **Confirm its regions** on that server. Every waiting region of
that server then belongs to it. This is written to the audit log. There is no keep-off for these: if the regions aren't
really that server's, remove the server; its regions stay pinned and waiting, so they stay off.

### No GridGate money

The region is on this computer or on a paired Region Server, but nothing has checked in with the money service for it.
It may run stock OpenSim, or money isn't set up for it. People can visit as usual; only money is off there.

**What to do:** run the region with GridGate's money set up, or leave it without money.

### Needs an update

The region's money module is from an older GridGate that can't say where people go, so money is off there.

**What to do:** update GridGate on that server, then restart its regions.

### Not heard from

The region's program hasn't checked in for over 10 minutes, so money is off there. Usually the region is stopped, still
starting, or its computer can't reach this one.

**What to do:** check the region is running, and that its computer can reach this one over the local network. Money comes
back within a few minutes of the region checking in again.

## Money paused for one person

Sometimes money is off for one person, even in a verified region. This happens when the money service can't be sure where
they are:

- **They came from, or went to, a region that can't verify** (stock OpenSim, an unpaired server, an older GridGate, or one
  that hasn't checked in). They travel normally, but money is paused for them from then on. When a verified region handed
  them on to one that never reported them, their page says "Moved to" that region.
- **Two servers disagreed about where they are.** Then neither is trusted, and a warning is raised (see [Alerts](#alerts)).
- **They arrived from another grid straight into a Region Server's region** (see
  [Visitor money and listed grids](#visitor-money-and-listed-grids)).

For residents, money comes back **at their next login**; for visitors, see
[Visitor money and listed grids](#visitor-money-and-listed-grids). Nobody can charge them while it is paused.

**Where you see it:** on a person's page (Money page, search the person), "Money here now" says whether money works for
them now and why not, with a **Why** link here. Someone who isn't on this grid right now, including a resident visiting
another grid, shows "Not in the world right now". Residents see a line on their **My coins** page saying money is paused until
they next log in, and the viewer tells them when a payment is refused (see
[What people see in the viewer](#what-people-see-in-the-viewer)).

**What to do:** usually nothing; ask them to log out and in again. If many people are paused after visiting the same
region, look at that region's status in the Regions list.

## Visitor money and listed grids

The **Visitors** section of the Money page decides whether people visiting from other grids can pay and be paid here:

| Choice | What it means |
|---|---|
| **Off** | No visitor can pay or be paid. |
| **Only grids I list** (the default) | Visitors from the grids on your list can. The list starts empty, so visitor money is off until you add a grid. |
| **All grids** | Any visitor the grid lets in can. |

Visitors can visit everywhere they're allowed whatever you choose; only money depends on it. Changing it needs an admin.

**Listing a grid.** A grid is listed by its home address, the one its residents' names show when they visit, for example
`http://hg.friendgrid.net:8002`. Listing a grid means **trusting the people who run it**: GridGate can't check that another
grid is honest, so only list grids whose operators you trust. Listing a grid trusts that grid only, not every grid its
visitors passed through on the way.

**Taking a grid off the list** freezes its visitors' coins where they are; nothing is taken away. Staff can still see those
wallets, and they work again if you list the grid again.

**If your grid used "Visitor wallets" before this update:** "on" now reads as **Only grids I list**, with an empty list, and
"off" as **Off**. Add the grids you want to the list.

**Keep your hypergrid welcome region on this computer.** Arrivals from other grids count for money only in the Grid Asset
Server's own regions. Someone arriving from another grid straight into a Region Server's region travels normally, but money
is paused for them for that visit: for a resident until their next login, for a visitor until they next come in through one
of this computer's regions. Arriving here first and then teleporting on to a Region Server keeps money working, because
the region they leave hands them on.

## Daily limits

Daily limits cap how many coins can move, even if everything else went wrong. A region program or Region Server that was
taken over could still charge the people actually standing in its regions; the limits cap how much.

| Setting | Default | What it caps |
|---|---|---|
| Daily spending limit per person | 10,000 | What regions may take from one person's wallet: pay, buy, land, parcel passes and fees. |
| Daily region limit per region program or Region Server | 100,000 | What all of one program's or server's regions may take from all wallets together. |
| Daily script payouts per region program or Region Server | 20,000 | What all of one program's or server's objects may pay out together. |
| Daily script payout limit per resident | 5,000 | What one resident's objects may pay out of their wallet. |
| Largest single payment | 1,000,000 | One payment. |

- **"A day" is the last 24 hours, rolling.** There is no midnight reset.
- **Purchases count when the coins are held,** and a purchase whose coins come back (the item didn't arrive) doesn't count.
- **Staff gifts, take-backs and refunds never count.** A refund doesn't make room under a limit again either; give the
  person a higher limit of their own if they need it.
- The limits apply in play-coin mode too. Owners running games can raise them.

Change the grid's defaults under **Settings** on the Money page; the **Limits** section shows them and how much each
program and server used in the last 24 hours. When a limit is reached, the payment is refused and the person is told (see
[What people see in the viewer](#what-people-see-in-the-viewer)); a server or program reaching its limit also raises an
alert.

## Own limits

The numbers above are grid-wide defaults. The owner can give one person, one region program or one Region Server limits of
its own, **higher or lower** than the defaults:

- **One region program or Region Server:** on the Money page, under **Limits**, "Each region program and Region Server". Set
  its daily region limit and its daily script payouts. For example, more for a trusted busy mall server, less for a new
  server nobody knows yet. Leave a box empty to use the grid default.
- **One person:** on their page, **Set this person's limits**. Set their daily spending limit and daily script payout limit,
  for example higher for a land sale, or lower when they ask. An end date is optional: the limits end at 00:00 UTC that day
  and go back to the defaults. Leave both boxes empty to go back to the defaults now.

Each change needs a reason and is written to the audit log, like a staff gift. Only the owner can set these; supervisors
can see them.

## Alerts

The money service raises an alert when something needs your attention. Open alerts are on the Money page under
**Alerts**; warnings also show on the dashboard. Every alert is written to the audit log. Each says what happened, where,
who was affected and what to do. Repeats are grouped: one line per region (or server, or person) per day, with a count.
Clear an alert once you've dealt with it.

| Alert | Level | What to do |
|---|---|---|
| **A region claimed someone arrived that it wasn't handed.** | A **notice** the first time, because a handoff lost on the network looks the same. A **warning** when one server or program does it three or more times in a day. | Check the region is running and its GridGate is up to date. If it charged someone it shouldn't have, refund the entry, or freeze their account on their page. If you don't trust the server, remove it. |
| **Two servers disagree about where someone is.** One server's region says the person arrived there, while the grid had them in another server's region. The alert names both: "Server X and server Y disagree about where Ann is." | Warning | When both regions are verified, money is paused for that person until their next login, so neither server can charge them. Either server may be the one that is wrong: check both regions are running and up to date, and look at what each charged them. Refund or freeze if needed. |
| **A server or program reached its daily limit.** | Warning | If that's expected, give it a higher limit of its own (see [Own limits](#own-limits)). If not, **freeze all payments** while you find out why. |
| **A region moved between servers.** | Notice | Approve the move or keep it off, under Regions (see [Moved, waiting for you](#moved-waiting-for-you)). |
| **A new pairing took over another server's address and ports.** A different computer paired at the same address and first port as a server that was paired before. The alert says the new server took over the earlier server's address and ports. | Notice | If you paired the new computer yourself, clear the alert. If not, remove the server: its regions take no payments until it is paired again. |
| **One person hit their daily spending limit three times in a day.** | Notice | If they asked for more, give them a limit of their own. If not, check what charged them; refund the entries or freeze their account. |

An alert row counts what one server does within a day: one row for its claims, however many regions they name, and one for each of the notices above.
A warning about a Region Server that reached its daily limit, or that claimed people it wasn't handed three or more times
in a day, offers **Remove this server**: its key ends at once, and its regions take no payments until it is paired again.
A disagreement between two servers doesn't offer it, since either may be the honest one. Refused claims never slow down or
block the server that made them, so an honest server isn't punished for a network fault; only you decide whether to remove
it.

## What people see in the viewer

When money is refused, the viewer shows a short message:

- "Money is off in this region: it can't verify who is here. You can still visit."
- "Money is paused for you until you next log in: you arrived from a region that can't verify where you are."
- "Visitors from your grid can't use money here. You're welcome to look around."
- "You've reached today's spending limit on this grid."
- "This region has reached today's money limit. Try again later."

## Regions and grids without GridGate

- **Stock OpenSim regions** on your grid run as normal and never take part in money. They show as "Not paired" when they
  are on a computer you haven't paired (a stock OpenSim computer can't be paired), or "No GridGate money" if they are on a
  paired Region Server or on this computer.
- **Older GridGate Region Servers** keep running. Their regions show "Needs an update" until updated.
- **People passing through a region that can't verify** keep travelling normally; money is paused for them until they next
  log in, and they're told why.
- **Visitors from stock OpenSim grids** arrive and travel as today. Money works for them only if you list their grid.
- **Your residents visiting other grids** aren't affected there. Coming home straight into a Region Server's region pauses
  their money until their next login.
