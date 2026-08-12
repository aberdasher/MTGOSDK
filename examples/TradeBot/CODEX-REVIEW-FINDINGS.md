# Codex correctness review — Negotiate engine (2026-08-12)

Reviewed `git diff e3fc901..HEAD` for CORRECTNESS ONLY (money-path bugs). Four
quality reviews (reuse/simplify/efficiency/altitude) had already run and missed all
of these — they are behavioural, not structural.

**STATUS (2026-08-12): the happy path is LIVE-VALIDATED with real tix.** Two deposits
committed through this engine (jobs `e39b9a662b28` 2x tix, `86e8bab6f946` 1x tix) and
the full tournament cycle ran on top of them: deposit -> entry-fee escrow -> prize
wallet -> payout, with the prize wallet draining to 0 and total claims (3) matching
the tix physically deposited. Custody recorded them as DEPOSITS, not loans.

**CORRECTED STATUS after a second review round (2026-08-12).** An earlier version of
this file claimed "findings 1-4 are FIXED" — that was an OVERCLAIM in two ways:

1. Findings 1-4 are fixed ONLY on the `Negotiate` path. The legacy CLI flows
   (`RunLend`, `RunSwapCycle`, `RunGrabCycle`, reachable from `lend`/`swap`/`grabfrom`)
   still use substring name matching, per-entry aggregation, and discard catIds. `lend`
   remains a name-gated commit path. Retiring those flows is the only thing that makes
   decision 8 true rather than aspirational.
2. The fixes themselves had holes, found by re-reviewing them (below).

Per-finding, for the ENGINE path: 1,2,3,4,5,6,8,9,10 addressed; **7 still open** (the
re-verify -> confirm window is still not one atomic UI-thread operation, so a partner
can in principle alter their side between the final guardrail read and the approve).
**8 is only partly closed**: we now abort when `SetLastUsedBinder` returns false, but we
never READ BACK the last-used binder to verify its id/contents, which is what the
finding asked for.

Round-2 fixes applied (holes in the round-1 fixes):
- Completion proof was GLOBAL: `CompletedTradeSeq` advances when ANY trade completes, so
  another trade finishing could "prove" ours did. Now escrow-scoped via
  `LastCompletedEscrowId`.
- A successful trade could be reported FAILED (the regression risk): the old loop broke
  after a single null + 1.5s grace. Now it polls the full window.
- Unreadable escrow rows became qty 0, hiding an unrequested item from extra-detection
  and letting a wrong trade look exact. Now a read failure makes the whole side
  unreadable (not-exact) — fail closed.
- Overlapping-but-not-identical printing sets (a pinned catId also present in another
  item's unpinned set) were aggregated independently, so each counted the same staged
  copy and a short receive looked exact. Now rejected at resolution with an actionable
  message.
- Give resolution could hand the SAME printing to two items and exceed what we own; the
  chosen quantity is now reserved as each item resolves.
- `qty <= 0` was clamped to 1 — a bogus request could move a real asset. Now rejected.

Several of these are PRE-EXISTING (inherited from the old flows via GiveStatus),
but the engine made them universal by routing every trade through one guardrail.

---

## CRITICAL — can commit a wrong trade

### 1. `NameMatches` is substring containment, not equality
`TradeExecutor.cs` (NameMatches; used by GiveStatus/ReceiveStatus).
Request `Island` -> escrow holds `Snow-Covered Island` -> **guardrail passes**.
Any requested name that is a substring of another card name can be satisfied by the
wrong card. Tix are low-risk ("Event Ticket" is unlikely to be a substring), cards
are not.
TENSION TO RESOLVE FIRST: operator CLI modes deliberately accept PARTIAL names
(`grabfrom bot Kozilek`). Fix must keep that ergonomic while making the GUARDRAIL
exact. Right shape: resolve the operator's partial name to a canonical card name
ONCE up front (at request construction), then guardrails compare normalized EQUALITY.

### 2. Aggregate comparison is per-entry, so quantities can be wrong both ways
`GiveStatus` / `ReceiveStatus`.
- Intended `[1x Island, 1x Island]`, escrow has ONE Island: each intended entry
  independently sees that one copy -> nothing reported missing -> **short receive
  commits**.
- Intended `[1x Island]`, escrow has TWO rows of Island (split printings) each qty 1:
  each row is individually within `allowed == 1` -> nothing reported extra ->
  **over-give commits**.
Fix: aggregate BOTH sides once into a canonical dictionary (key: catId when pinned,
else normalized name) and compare for exact equality.

### 3. Pinned printings (catId) are dropped by every commit guardrail
`Program.cs` — `giveIntended`/`recvIntended` are built as `(name, qty)`, discarding
`catId`. A recall pinned to printing A accepts printing B at approval.
Consequence: **a loan is settled without the lent copy coming back**.
Fix: carry `(name, qty, catId)` through GiveStatus/ReceiveStatus; when catId > 0
require that exact catalog id.

### 4. An unowned pinned GIVE printing silently substitutes another
`TradeExecutor.ResolveOwnedPrintings` falls back to any owned printing when the
pinned catId isn't owned. Correct for unpinned requests; for a PINNED give it means
handing over a printing we were not authorised to give.
Fix: `wantCat > 0` -> fail unless that exact catId is owned in sufficient quantity.

### 5. The watch loop can adopt a DIFFERENT escrow
`Program.cs` — `esc = c;` takes whatever `TradeManager.CurrentTrade` returns each
tick. If the intended trade closes and another opens between polls, the engine can
proceed against the wrong partner.
Fix: capture escrow id + normalized partner at open; require both to match at every
poll, submit, re-verify, and confirm; otherwise cancel and fail.

---

## HIGH

### 6. `FinalizeTrade` can verify one escrow and confirm another
The guardrail runs against the escrow it read, but `ConfirmTrade` independently
resolves the live trade view-model. Fix: pass the expected escrow id in and confirm
only that id.

### 7. Unguarded re-verify -> confirm window
The partner can change their side between the final guardrail read and the confirm
dispatch. Today safety rests on MTGO invalidating approval fast enough, not on our
code. Fix: identity check + snapshot verify + approval-state check + confirm in ONE
UI-thread operation.

### 8. Binder-selection failure is ignored (fails OPEN)
`Program.cs` / `TradeExecutor.SetLastUsedBinder` — if setting the binder returns
false, `AdvanceBinderSelection` still runs and MTGO presents the PREVIOUS binder.
This is exactly the "random stuff in the binder" problem the Offer/Empty work was
meant to end, and it violates the fail-closed decision.
Fix: abort unless the set succeeds; read back the last-used binder and verify its id
+ contents before advancing.

---

## MEDIUM

### 9. `FinalizeTrade` returns TRUE without proving the trade completed
`ConfirmTrade` returns void; if `ConfirmTradeCanExecute` is false, or the trade never
closes within 40s, `FinalizeTrade` still returns true.
Consequence: **the job is marked done and DraftBot credits a deposit that never
happened** (and recall calls `SettleReturn` for a card that never came back). This is
ledger corruption, not just a bad log line.
Fix: `ConfirmTrade` returns whether it dispatched; require the expected escrow to
close with `FinalState == TradeComplete`; anything else returns false.

### 10. A transient null read abandons a live trade without cleanup
`Program.cs` — a momentary `CurrentTrade == null` (which `TryReachNegotiation`
explicitly tolerates) returns false immediately, leaving our offer exposed in a still
-open trade. A throw on that read is uncaught too.
Fix: tolerate a bounded number of nulls against the expected escrow id; on real
disappearance, cancel + require `WaitForNoTrade()`.

---

## Verified safe (no action)
- both-empty request is rejected by RunTrade.
- empty-side status logic is otherwise fail-closed.
- insufficient owned quantity fails binder exactness rather than presenting extras.
- premature partner-binder snapshot can cancel early, but cannot cause a wrong commit.
