# DraftBot × MTGO TradeBot — Tix Wallet, Debt Settlement, Card Lending

Plan doc — 2026-08-01. **Status: awaiting approval before implementation.**
Custodian account: **Sealed01**. Serve API: `examples/TradeBot` `serve` mode (built + tested;
give/deposit/jobs, bearer auth, auto-reconnect).

## Goal
Let DraftBot manage tix debts (and card loans) through the MTGO TradeBot serve API, so
settlement is automated instead of manual bookkeeping — with the **fewest possible MTGO trades**.

## Core model — a custodian bank/vault
Sealed01 holds tix + cards on behalf of players. **Only moving value in/out of MTGO
(deposit / withdraw) needs a trade.** Everything internal (settling debts, lending cards
between players) is a ledger op with no trade.

### Two ledgers: obligations vs physical (the mental model)
There are **two ledgers answering two different questions**, and the bridge between them is where
the work is:

- **Obligation ledger** — *who is owed what.* Append-only intent: "bot owes A a Griselbrand",
  "loser owes winner 3 tix", "B owes bot a Griselbrand". Never moves a card by itself.
- **Physical ledger** — *what the bot actually holds.* The vault's real MTGO contents (ground truth
  = Sealed01's collection). **Built:** holdings + vault + trade log.

**Update vs resolution** are distinct operations:
- **Update (accrue):** book a new obligation (a draft ends; a deposit lands). Obligation ledger
  only — no trade.
- **Resolution (settle):** fulfil an obligation. **Most resolutions are internal claim-moves, not
  trades** — a trade fires *only* when value crosses the bot boundary (deposit in / withdraw out).
  - *Lazy (fungible / tix):* re-assign the claim (A −N, B +N); the tix never leave the vault → 0 trades.
  - *Eager (cards to play):* the card must reach the borrower's client, so a lend is a real
    bot→borrower trade.

**Lending lifecycle** (A deposits Griselbrand, then it's lent to B):

| Event | Physical | Obligation booked | A's claim backing |
|---|---|---|---|
| Deposit A→bot | +card in vault | **bot ⟶ A** | funded (card in vault) |
| Lend bot→B | −card (to B) | **B ⟶ bot** (only new entry) | receivable (backed by B) |
| Return B→bot | +card in vault | clear **B ⟶ bot** | funded again |
| Withdraw A→out | −card (to A) | clear **bot ⟶ A** | — |

The `bot ⟶ A` debt is **booked at the deposit** and just persists, changing only its *funding
status* as the card moves on/off loan. One `Holding{owner:A, borrower:B}` row carries both debts
(`owner` = the bot⟶A debt; `borrower` set = the B⟶bot debt *and* the "A is now a receivable" signal).

**Reconciliation invariant:** at every instant each **"bot owes"** is backed by *either* inventory
*or* a matching **"owes bot"** — so `physical == Σ funded claims`, and the bot's own net position is
always **flat** (pure intermediary; the real obligation is B ⟶ A routed through the bot).

**Built vs missing:** physical ledger ✅. Tix wallets (fungible claim ledger) ❌; debt accrual from
draft results ❌; the **resolution engine** (read obligations → decide internal-move vs trade) ❌.
First brick: the tix wallet + the `physical == Σ claims` audit — resolution needs claims to move
between before any of the internal-settle logic makes sense.

### Tix wallet
Each player has a wallet balance = tix stored in the system.

| Op | MTGO trade? | Effect |
|---|---|---|
| Deposit N | yes (bot receives) | wallet +N |
| Withdraw N | yes (bot gives) | wallet −N (needs balance) |
| Pay N to B / settle a debt | no (internal) | wallet −N, B +N, debt cleared |

- **Auto-draw:** a debtor's wallet balance is automatically applied to their outstanding
  debts **oldest-first**, on deposit AND on debt creation — no manual `/pay` needed. Net
  position = balance − debts. `/pay` remains for sending tix without a pre-existing debt.
- **Invariant:** bot's on-MTGO tix balance == `SUM(all wallet balances)` (auditable).

### Draft debts (existing, unchanged)
`DebtLedger` books `loser owes winner N tix` at draft end. Settlement now flows through the
wallet (auto-draw / pay) instead of the manual `/settle` bookkeeping.

## MTGO serve-API prerequisites
1. **Tix as a tradable item** — verify "Event Ticket" resolves + moves through give/deposit
   (tix are MTGO currency, not a normal card); pin its catId. **Do this first — blocks everything.**
2. **Quantity** — give already supports qty; **add real qty to deposit** (receive N of one item).
   New shape: `POST /deposit {user, item, qty, commit}` (mirror qty on `/request`).
3. **Reachability** — bind serve to the **Tailscale** IP (`--bind=`); bearer token already enforced.

## DraftBot changes
1. **Account link** — `MtgoAccount` (Discord id ↔ MTGO username) + `/link_mtgo`. Built earlier on
   an unmerged branch → **reconcile that first**, don't rebuild.
2. **TradeBot client** — `services/mtgo_tradebot_client.py`: aiohttp + `Authorization: Bearer`;
   `deposit()`, `request()`, `get_job()`; config `MTGO_TRADEBOT_URL` + `MTGO_TRADEBOT_TOKEN`
   (mirrors the `helpers/magicprotools_helper.py` idiom).
3. **Wallet state** — `TixWallet(player_id, guild_id, balance)` + `WalletTx` audit log
   (`kind ∈ deposit|withdraw|pay|receive`, `amount`, `counterparty_id`, `job_id`, `debt_source`, `created_at`).
4. **Async job tracking** — a background asyncio poller per job (`GET /jobs/{id}`); **ledger writes
   ONLY when a job reports `done`** (never on enqueue); idempotent by `job_id`; a withdraw
   **reserves** balance while its job runs (no double-spend), releasing on failure.
5. **Commands** — `/wallet` (balance + history), `/deposit`, `/withdraw` (MTGO trades), `/pay`
   (internal), plus "pay from wallet" on the debt board. Replaces the manual `/settle` bookkeeping.
6. **Gating** — `is_money_server()` + `is_test_mode()`; both parties must be linked.

## Phasing
- **Phase 0 — prereqs:** serve-API tix + multi-qty deposit + Tailscale bind; DraftBot `MtgoAccount`
  (reconciled) + client + config. *(No user-facing behavior.)*
- **Phase 1 — thin slice:** deposit → wallet balance, end-to-end behind a flag. Proves
  item + qty + client + job-poll + ledger with the lowest-risk leg.
- **Phase 2 — full loop:** withdraw + internal `/pay` + **auto-draw** settlement; wire the debt
  board. **+ Card lending (below).**
- **Phase 3 — polish:** notifications, public board, retries/partial payments, audit
  (`SUM(wallets)` vs bot tix), overdue-loan reminders.

## Phase 2 extension — Card lending
Symmetric to the tix wallet, for cards. The custodian holds cards for players; only deposit /
withdraw hit MTGO; lending/returning between players is internal.

- **State:**
  - `CardHolding(player_id, guild_id, card_name, catId, qty)` — a player's cards stored in the bot.
  - `CardLoan(id, lender_id, borrower_id, card_name, qty, status ∈ active|returned, loaned_at, returned_at, guild_id)`.
- **Ops:**
  - **Deposit card** (MTGO receive) — store cards → holding +card. *(reuses the `grabfrom`/receive flow)*
  - **Withdraw card** (MTGO give) — take a card out to play → holding −card. *(reuses the `lend`/give flow — live-tested with Kozilek's Command)*
  - **Lend card → B** (internal, no trade) — move a card from your holding to B's, open a `CardLoan`. B can then withdraw it to play with.
  - **Return** (internal) — close the loan, card moves back to the lender's holding.
- **Only deposit + withdraw are trades** — same minimal-trade benefit as tix.
- **Commands:** `/card deposit`, `/card withdraw <card>`, `/lend <card> @player`, `/return <card>`, `/inventory`, `/loans`.
- **Model (confirmed): peer inventories, permissioned.** Each player stores + lends their OWN
  cards (symmetric to the wallet). Lending is NOT open — a lender authorizes specific borrowers:
  - `BorrowPermission(lender_id, borrower_id, guild_id, card_name NULL = any card)` — B may borrow
    A's card only if A granted it (globally or per-card). Commands `/lend-allow @player [card]` /
    `/lend-revoke @player [card]`; a borrow request checks this before the internal transfer.

## Risks / correctness
- Custodian of **real value** → the `SUM(wallets) == bot tix` audit and **ledger-only-on-committed-job**
  writes are the core correctness properties.
- **Cooperation:** deposit/withdraw need the player online + confirming in-client (assisted, not forced).
- **Tix mechanics** must be verified before anything else.
- **Withdraw double-spend** (reserve balance during the job); **interrupted jobs** (job status is the
  source of truth — poll to a terminal state before touching the ledger).
- **Card loans:** overdue tracking; cards aren't fungible, so loans are per-card/qty.

## Open items to confirm
- Which repo/branch is current DraftBot main + where the earlier `MtgoAccount` work lives (confirm first).
- Card lending flavor (peer inventories vs shared library) — recommend peer inventories.
- Whether the card vault should be Sealed01 too or a separate account.
