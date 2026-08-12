# Negotiate(give[], receive[]) — unified trade engine

## DECISIONS (settled 2026-08-11, build to these)

1. Counterparty wording is a FIRST-CLASS requirement: every DM must tell the partner
   exactly how to resolve the current state — "pick a binder containing Y BEFORE
   accepting, then reply YES", "still to grab from my binder: X", "wrong printing —
   present the copy with id N". Prompts are parameterized per shape (a deposit says
   "deposit", a lend says "grab"), never a generic "trade incomplete".
2. NO legacy-fallback flag. Hard cutover; rollback = git revert to the previous
   branch state. Wrappers delegate to Negotiate immediately.
3. Watch phase inherits the job's WaitSec (like the handshake) — no new fixed bound.
   Operator abort (AbortRequested) remains the escape everywhere.
4. All-or-nothing fills, stated explicitly: short/extra on either side at approval =
   cancel with instructive DM. (DraftBot ledger arithmetic requires exact amounts.)
5. Fix the per-copy binder resolution IN phase 1: one collection scan into a
   Dictionary<catId,qty> / name->cats map, resolve all give[] items against it
   (a 50-tix payout must not cost 100 scans).
6. Binder presentation is FAIL-CLOSED: if the Offer binder can't be built and
   confirmed as last-used before the invite, cancel the job with a clear detail —
   never open a trade showing an unknown binder.
7. Partner-side binder choice is theirs alone (MTGO fixes it at trade start):
   the retry dance (cancel -> instruct -> YES -> reopen) is the accepted UX.

One pipeline for every trade shape. Lend/swap/grab become parameterizations;
the v1 "unsupported combo" rejection disappears; every trade presents a binder
containing EXACTLY what the partner may take (empty for receive-only).

## Signature

```csharp
static bool Negotiate(TradeBot.TradeExecutor exec, string partner,
    List<(string name, int qty, int catId)> give,
    List<(string name, int qty, int catId)> receive,
    bool allowCommit, int yesTimeoutSec, string? doneDm = null)
```
Returns true iff committed. Always sets TradeExecutor.LastFlowDetail (real reason).

## PHASE 1 — DONE + PROVEN LIVE (2026-08-11, `testbinder` mode)

Results against a live client (no trade opened, nothing moved):
- empty give -> binder 'Empty' auto-created, 0 items: **PASS** (partner sees nothing)
- give 2x Event Ticket -> binder 'Offer', exactly 2, pinned printing catId=1: **PASS**
- give 5x Event Ticket (preResolved path): **PASS**
- failure path verified: when no binder could be prepared it FAILED CLOSED.

KEY FINDING (cost two wrong conclusions — do not re-learn the hard way):
MTGO *can* create an empty binder via CreateNewBinder(name, null, emptyList). The
early failures were `System.TimeoutException: Dispatcher operation timed out after
5 seconds. The WPF UI thread may be blocked.` — a TRANSIENT cold-client failure,
surfaced only in the INNER exception. Two consequences, both now in the code:
  * ALWAYS `WarmCollectionAndBinders()` before binder work. The binder list loads
    SLOWER than the collection; a blank binder list means "not ready", not "none".
  * ALWAYS log the full exception chain — the outer DynamicObject message says
    nothing. Retry through these timeouts (observed: fails twice, succeeds on 3rd).

Binder API surface (probed): the ONLY overload is
`IBinder CreateNewBinder(String name, IVisualResource binderImage, IEnumerable initialCards)`
— no add/remove/clear item methods exist, so a binder's contents can only be set at
creation (hence delete+recreate for exact contents).

## Phases

1. OFFER BINDER — always present a binder named "Offer" containing exactly give[]:
   - give[] empty  -> EnsureEmptyBinder("Offer")  (deposits/grabs present NOTHING)
   - give[] items  -> EnsureBinderExact("Offer", give) (exact printings via catId when >0)
   - Reuse the existing Lending-binder machinery (CreateBinder/EnsureBinderExact path);
     add "Offer" to PruneBinders' transient set alongside "Lending"/"SwapOffer".
2. HANDSHAKE — SendPromptWhenOnlineAndWaitForYes(partner, prompt, yesTimeoutSec)
   where prompt describes both sides ("I'm offering X; please present Y; reply YES").
   Then CancelCurrent() + WaitForNoTrade() + TryReachNegotiation(exec, partner,
   presentBinder: "Offer", negotiateWaitSec: 90).
3. WATCH LOOP (1s tick, bounded ~180s, abort-aware):
   - Snapshot PartnerCollection ONCE after open (their presented binder is FIXED at
     trade start — retry a few ticks while it populates). Resolve each receive[] item
     against the snapshot: prefer catId match (recall/exact printing), else normalized
     name match (the U+2019 apostrophe Norm from GiveStatus).
   - Request each receive[] item once (the TakeCard/request path); re-request only on
     evidence it didn't stage (grab's matchedEver && !haveIt stale-VM detection ->
     cancel with "stale trade view-model, client restart needed").
   - Per tick guardrail: GiveStatus(esc, give).exact AND new ReceiveStatus(esc,
     receive).exact (mirror of GiveStatus for the partner side; same Norm matching,
     same missing/extra shape). Both empty-side cases degenerate correctly
     (give=[] -> "we give nothing"; receive=[] -> "we take nothing").
   - Reminder DMs on a slow cadence listing what's still missing (from RunLend).
   - PartnerHasSubmitted early-submit handling (from RunLend): if they submit before
     completing, remind once per state change.
   - Every cancel sets LastFlowDetail with the specific reason + DMs the partner why.
4. FINALIZE — the existing FinalizeTrade(exec, partner, c => GiveStatus(c, give).exact
   && ReceiveStatus(c, receive).exact, allowCommit, label, doneDm). Already shared.

## Executor additions

- ReceiveStatus(esc, intended) -> (missing, extra, exact): the receive-side mirror of
  GiveStatus. Replaces VerifyReceiveIsOnly at Negotiate call sites (that method can
  stay for now; delete when unreferenced).
- EnsureEmptyBinder(name): create-or-empty a binder with zero cards.

## Routing

- RunTrade: ALL shapes -> Negotiate(give, receive, ...). Delete the three-way
  dispatch and the v1 rejection branch. okDetail/failDetail from LastFlowDetail.
- Keep thin compatibility wrappers (RunLend/RunGrabFlow/RunSwapFlow) delegating to
  Negotiate so listen desks, lendretry, and recallFn keep working; collapse callers
  onto Negotiate opportunistically later.
- recall = Negotiate(give: [], receive: [item with requiredCat]) — already how
  recallFn shapes it; the LoanId settle stays in the serve worker.
- qty>1 receive, multi-give, give+multi-receive: all now legal (the point).

## Ported edge cases (do not lose)

- grab: requiredCat exact-printing enforcement; stale-VM cancel; "not in presented
  binder" cancel with retry DM.
- lend: qty-aware set matching (3x across split entries); reminder DMs; early-submit.
- swap: both-sides exactness; per-want normalized matching.
- all: dry-run (no --commit) cancels at approval with nothing moved; operator abort
  (AbortRequested) at every wait.

## Custody/serve integration (already done, no changes needed)

- TradeCompleted event fires on commit; serve/CLI recorders handle intent
  (withdraw vs loan). Negotiate itself records nothing.
- Serve /trade jobs route through RunTrade -> Negotiate; intent field already
  plumbed.

## Test plan (dry-run first, always)

1. Build; `probe` mode sanity.
2. Dry-run receive-only vs a second account: verify EMPTY "Offer" binder is
   presented (partner sees nothing to take), request stages, approval reached,
   dry-run cancels clean.
3. Dry-run give-only (1 tix): partner sees exactly 1 tix, nothing else.
4. Live 1-tix deposit + 1-tix withdraw through DraftBot (the real e2e).
5. Live card lend + recall (exercise catId path + loan custody).
6. Only then: retire dead per-flow code paths.
