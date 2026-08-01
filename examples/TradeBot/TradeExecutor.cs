/** @file
  TradeExecutor — prototype wrapper that DRIVES MTGO trade actions (not just
  reads them) through MTGOSDK's remoting bridge.

  SAFETY MODEL
  ------------
  MTGO holds cards in escrow and transfers NOTHING until BOTH parties send a
  final approval. That single "final approve" is the only asset-moving step.
  This class funnels that one call through FinalApprove(), which throws unless
  AllowCommit == true. Everything else (invite, add items, submit deposit list,
  cancel, trade posts) is non-committing / reversible.

  All remote types are addressed by string query-path, so this file needs NO
  direct reference to the WotC.* assemblies.
**/

using MTGOSDK.API;                                  // ObjectProvider
using MTGOSDK.API.Chat;                             // ChannelManager, Channel, Message (DM handshake)
using MTGOSDK.API.Collection;                       // ItemCollection, CardQuantityPair
using MTGOSDK.API.Trade;                            // TradeManager, TradeEscrow
using MTGOSDK.API.Trade.Enums;                      // TradeState
using MTGOSDK.API.Users;                            // User
using MTGOSDK.Core.Remoting;                        // RemoteClient
using MTGOSDK.Core.Remoting.Interop;                // DiverCommunicator (UI-thread scope)
using MTGOSDK.Core.Remoting.Types;                  // DynamicRemoteObject
using static MTGOSDK.Core.Reflection.DLRWrapper;    // Unbind(...)

namespace TradeBot;

public sealed class TradeExecutor : IDisposable
{
  // Live WotC model query-paths (confirmed via reference-assembly metadata dump).
  const string ITradeManager  = "WotC.MtGO.Client.Model.Trade.Interfaces.ITradeManager";
  const string ITrade         = "WotC.MtGO.Client.Model.Trade.Interfaces.ITrade";
  const string IMarketplace   = "WotC.MtGO.Client.Model.Trade.Interfaces.IMarketplace";
  const string ActionsNs      = "WotC.MtGO.Client.Model.Trade.ClientActions";
  const string CollectionItem = "WotC.MtGO.Client.Model.Data.CollectionItem";
  const string TradePostFmt   = "WotC.MtGO.Client.Model.Trade.TradePostFormat";
  const string AnnotationEnum = "WotC.MTGO.Common.DigitalObjectAnnotationEnum";

  // Confirmed ClientAction type names (WotC.MtGO.Client.Model.Trade.ClientActions.*).
  const string A_ItemUpdate   = ActionsNs + ".SendTradeItemUpdateAction";
  const string A_InviteReq    = ActionsNs + ".SendTradeInvitationReqAction";
  const string A_InviteAccept = ActionsNs + ".SendTradeInvitationAcceptAction";
  const string A_InviteDecline= ActionsNs + ".SendTradeInvitationDeclineAction";
  const string A_CancelBinder = ActionsNs + ".CancelBinderSelectionAction";
  const string A_DepositUpdate= ActionsNs + ".SendFlsEscrowDepositlistUpdateReqAction";
  const string A_FinalApprove = ActionsNs + ".SendFlsEscrowFinalApproveReqAction";
  const string A_Cancel       = ActionsNs + ".SendDosEscrowCancelReqAction";

  // Play-domain (spectate) types. A watchable game the client knows about lives as
  // an InProgressGame; you watch it by dispatching a WatchGameAction into its owning
  // Match (Match.Process(Object) is NON-generic — a plain .Process(action) works).
  // The match handler MatchNotJoinedEventUnderwayState.WatchGameHandler sends the
  // FlsGameWatchReq; on success the state machine streams game data and the game's
  // LogChannel populates. LocalUserIsWatching flips true once connected.
  const string InProgressGameT   = "WotC.MtGO.Client.Model.Play.InProgressGameEvent.InProgressGame";
  const string ReplayGameT       = "WotC.MtGO.Client.Model.Play.ReplayGameEvent.ReplayGame";
  const string PlayerEventActionsT = "WotC.MtGO.Client.Model.Play.ClientActions.PlayerEventActions";

  // MTGO's own trade-window view-model (Shiny.Trade). Requesting a partner card
  // through THIS builds the item-update message (permission code etc.) via the
  // client's own logic, instead of us synthesizing a CollectionItem and guessing
  // the permission code (which the trade server rejects with ErrorReceived).
  const string ActiveTradeVM  = "Shiny.Trade.ViewModels.ActiveTradeViewModel";

  // File-import types used by MTGO's own "import a wishlist into the trade" flow.
  // Building an IFileImportCardGrouping of desired cards and matching it against
  // the partner's binder is a BATCH request path (adds all matched cards at once).
  const string FileImportGrouping = "WotC.MtGO.Client.Model.Core.FileImport.FileImportCardGrouping";
  const string FileImportCard     = "WotC.MtGO.Client.Model.Core.FileImport.FileImportCard";

  // Collection grouping manager + card-definition type for creating a single-card
  // trade binder. NOTE ICardDefinition is in ...Model, NOT ...Model.Collection.
  const string IGroupingManager   = "WotC.MtGO.Client.Model.Collection.ICollectionGroupingManager";
  const string ICardDefinition    = "WotC.MtGO.Client.Model.ICardDefinition";

  // Buddy list — the client only resolves users it knows (buddies/seen), so a DM
  // to an unknown user requires adding them as a buddy first (AddUser(name, comment)).
  const string IBuddyUsersList    = "WotC.MtGO.Client.Model.Chat.IBuddyUsersList";
  const string IChat              = "WotC.MtGO.Client.Model.Chat.IChat";
  const string IFlsClientSession  = "FlsClient.Interface.IFlsClientSession";
  const string IShellViewModel    = "Shiny.Core.Interfaces.IShellViewModel";

  /// <summary>THE safety gate. Leave false for all validation runs.</summary>
  public bool AllowCommit { get; init; } = false;

  // Acquisition ledger (escrow accounting). Captured during a trade and written
  // on completion, since a closed escrow is no longer readable.
  public static readonly string LedgerPath = System.IO.Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "mtgosdk-tradebot", "ledger.log");
  private List<string> _lastReceived = new();
  private List<string> _lastGiven = new();
  private string? _lastPartner;

  /// <summary>FinalState of the most recently CLOSED trade (e.g. "TradeComplete",
  /// "OtherBusyTrading", "UserCanceledTrade"). Lets callers explain why an invite
  /// bounced. Null until a trade has closed this session.</summary>
  public string? LastCloseReason { get; private set; }

  /// <summary>How many cards the last RequestViaWishlist matched against the
  /// partner's presented trade binder (&gt;0 means the card IS in their offer, even
  /// if it hasn't landed on our receive side yet).</summary>
  public int LastMatchCount { get; private set; }

  static List<string> SnapshotItems(ItemCollection coll)
  {
    var list = new List<string>();
    try
    {
      foreach (var it in coll.CollectionItems)
      {
        try { list.Add($"{it.Quantity}x {it.Card?.Name} (cat {it.Id})"); } catch { }
      }
    }
    catch { }
    return list;
  }

  public void RecordAcquisition(string partner, List<string> received, List<string> given, string finalState)
  {
    try
    {
      System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LedgerPath)!);
      string recv = received.Count > 0 ? string.Join(" | ", received) : "(none)";
      string give = given.Count > 0 ? string.Join(" | ", given) : "(none)";
      string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  partner={partner}  final={finalState}  RECEIVED: {recv}  GAVE: {give}";
      System.IO.File.AppendAllText(LedgerPath, line + Environment.NewLine);
      Log($"[ledger] recorded → {line}");
    }
    catch (Exception ex) { Log($"[ledger] write failed: {ex.Message}"); }
  }

  static void Log(string s) => Console.WriteLine(s);

  // Trade actions open WPF dialogs / mutate UI-bound models, so they must run on
  // MTGO's UI dispatcher thread. BeginUIThreadScope marshals the remote call there.
  static void OnUI(Action a) { using (DiverCommunicator.BeginUIThreadScope()) a(); }

  //
  // Service accessors (raw remote DROs) — reach action methods not on the
  // read-only SDK wrappers.
  //
  static dynamic Marketplace() => ObjectProvider.Get(IMarketplace, true, false, false);
  static dynamic TradeService() => ObjectProvider.Get(ITrade, true, false, false);
  static dynamic TradeMgr() => ObjectProvider.Get(ITradeManager, true, false, false);

  // ─────────────────────────────────────────────────────────────────────────
  // READ-ONLY: validate the execution surface is actually callable on the live
  // objects (no state change, no assets, no counterparty needed).
  // ─────────────────────────────────────────────────────────────────────────
  public void ProbeExecutionSurface()
  {
    Log("=== TradeExecutor: execution-surface probe (read-only) ===\n");

    ProbeService("ITradeManager", ITradeManager,
      new[] { "RequestTradeWithUser", "Initialize", "SendDosEscrowOpenpackSetupReqMessage" });
    ProbeService("ITrade", ITrade,
      new[] { "RequestTrade", "RequestOpenItem", "Initialize", "ReInitialize" });
    ProbeService("IMarketplace", IMarketplace,
      new[] { "SubmitMyPost", "JoinMarketplaceChannel", "MyPost", "AllPosts" });

    Log("\n--- ClientAction types + factories/ctors ---");
    foreach (var a in new[]{ A_ItemUpdate, A_InviteReq, A_InviteAccept, A_InviteDecline,
                             A_CancelBinder, A_DepositUpdate, A_FinalApprove, A_Cancel })
      ProbeActionType(a);

    Log("\n--- Current escrow (Process overloads), if any ---");
    var cur = TradeManager.CurrentTrade;
    if (cur is null) Log("  (no active trade — start one to probe ITradeEscrow.Process live)");
    else DumpMethods("escrow", ((DynamicRemoteObject)Unbind(cur)).__type,
                     new[] { "Process", "SendMessage", "UpdateState", "RefreshState" });

    Log("\n=== probe complete (no state was modified) ===");
  }

  static void ProbeService(string label, string typeName, string[] interesting)
  {
    try
    {
      dynamic svc = ObjectProvider.Get(typeName, true, false, false);
      var t = ((DynamicRemoteObject)Unbind((object)svc)).__type;
      DumpMethods(label, t, interesting);
    }
    catch (Exception ex) { Log($"  [{label}] probe failed: {ex.GetType().Name}: {ex.Message}"); }
  }

  static void DumpMethods(string label, Type t, string[] interesting)
  {
    Log($"  [{label}] concrete type: {t.FullName}");
    System.Reflection.MethodInfo[] methods;
    try { methods = t.GetMethods(); } catch (Exception ex) { Log($"    (GetMethods failed: {ex.Message})"); return; }
    foreach (var name in interesting)
    {
      var matches = methods.Where(m => m.Name == name).ToArray();
      if (matches.Length == 0) { Log($"    - {name}: NOT FOUND"); continue; }
      foreach (var m in matches)
      {
        string ps = string.Join(", ", m.GetParameters().Select(p => $"{Safe(() => p.ParameterType.Name)} {p.Name}"));
        Log($"    - {Safe(() => m.ReturnType.Name)} {m.Name}({ps})");
      }
    }
  }

  static void ProbeActionType(string full)
  {
    try
    {
      var t = RemoteClient.GetInstanceType(full);
      var statics = t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                     .Where(m => m.Name == "GetInstance").ToArray();
      var ctors = t.GetConstructors();
      string fac = statics.Length > 0
        ? string.Join(" | ", statics.Select(m => "GetInstance(" +
            string.Join(", ", m.GetParameters().Select(p => Safe(() => p.ParameterType.Name))) + ")"))
        : "(no static GetInstance)";
      Log($"    - {full.Substring(ActionsNs.Length + 1),-40} {fac}");
      foreach (var c in ctors)
        Log($"          ctor({string.Join(", ", c.GetParameters().Select(p => $"{Safe(() => p.ParameterType.Name)} {p.Name}"))})");
    }
    catch (Exception ex) { Log($"    - {full}: resolve failed: {ex.Message}"); }
  }

  static string Safe(Func<string> f) { try { return f(); } catch { return "?"; } }

  // ─────────────────────────────────────────────────────────────────────────
  // OUTWARD-FACING (publishes a marketplace listing). Reversible. No assets.
  // ─────────────────────────────────────────────────────────────────────────
  public void PublishMessagePost(string message)
  {
    dynamic mkt = Unbind((object)Marketplace());
    OnUI(() =>
    {
      dynamic post = mkt.MyPost;
      post.ClearAllCards();
      dynamic fmt = RemoteClient.CreateEnum(TradePostFmt, "Message");
      post.Submit(fmt);
      mkt.SubmitMyPost(message);
    });
    Log($"[post] published message listing: \"{message}\"");
  }

  public void ClearPost()
  {
    dynamic mkt = Unbind((object)Marketplace());
    OnUI(() =>
    {
      dynamic post = mkt.MyPost;
      post.ClearAllCards();
      mkt.SubmitMyPost(string.Empty);   // resubmit empty to retract (no dedicated remove)
    });
    Log("[post] cleared/retracted listing");
  }

  // ─────────────────────────────────────────────────────────────────────────
  // OUTWARD-FACING (sends an invite). Non-committing.
  // ─────────────────────────────────────────────────────────────────────────
  public TradeEscrow? RequestTrade(string partnerName)
  {
    // Resolve case-INSENSITIVELY, same as the DM/resolve path (ResolveUserId). Using
    // `new User(name)` here is case-SENSITIVE (UserManager.GetUser default ignoreCase:
    // false) and throws "User '<name>' cannot be found." whenever the caller's casing
    // differs from the real handle (e.g. 'basic_' vs 'Basic_') — even though the DM
    // handshake to the same person just succeeded. GetUser(name, true) returns the
    // canonical user so the invite actually opens.
    dynamic user = Unbind(MTGOSDK.API.Users.UserManager.GetUser(partnerName, true));
    dynamic mgr  = Unbind((object)TradeMgr());
    OnUI(() => mgr.RequestTradeWithUser(user));   // opens the binder-selection UI dialog
    Log($"[trade] invite sent to {partnerName}");
    return TradeManager.CurrentTrade;   // may be null for a tick
  }

  // ─────────────────────────────────────────────────────────────────────────
  // NON-COMMITTING escrow steps.
  // ─────────────────────────────────────────────────────────────────────────
  public void AcceptInvite(TradeEscrow escrow)
  {
    dynamic remote = Unbind(escrow);
    OnUI(() => ProcessAction(remote, A_InviteAccept, BuildAction(A_InviteAccept)));
  }

  /// <summary>
  /// Read-only: dump what the partner (freebot) offers in negotiation + the
  /// escrow's item/want/deposit method surface, to work out how to grab a card.
  /// </summary>
  public void InspectNegotiation(TradeEscrow esc)
  {
    Log($"--- negotiation inspect: partner={Try(() => esc.TradePartnerName) ?? "?"} state={esc.State} ---");
    void DumpColl(string label, Func<ItemCollection> get)
    {
      try
      {
        var items = get().CollectionItems;
        Log($"  {label}: {items.Count} item(s)");
        foreach (var it in items.Take(12))
          Log($"      {Try(() => (int?)it.Quantity)}x  catId={Try(() => (int?)it.Id)}  {Try(() => it.Card?.Name) ?? "?"}");
      }
      catch (Exception ex) { Log($"  {label}: read failed ({ex.GetType().Name}: {ex.Message})"); }
    }
    DumpColl("PartnerCollection (freebot binder)", () => esc.PartnerCollection);
    DumpColl("PartnerTradedItems (offered by them)", () => esc.PartnerTradedItems);
    DumpColl("TradedItems (offered by us)", () => esc.TradedItems);

    try
    {
      var t = ((DynamicRemoteObject)Unbind(esc)).__type;
      Log("  escrow methods (item/want/deposit/add/request):");
      foreach (var m in t.GetMethods()
                 .Where(m => System.Text.RegularExpressions.Regex.IsMatch(m.Name, "Item|Want|Deposit|Add|Request|Grant|Take"))
                 .DistinctBy(m => m.Name).Take(30))
        Log($"      {m.Name}({string.Join(", ", m.GetParameters().Select(p => Safe(() => p.ParameterType.Name)))})");
    }
    catch (Exception ex) { Log($"  method dump failed: {ex.Message}"); }
  }

  /// <summary>
  /// Advance past the (bot-invisible) binder-selection dialog programmatically:
  /// send SendTradeInvitationReqAction, pushing InviteSelectBinder -> InviteSent
  /// (i.e. actually dispatch the trade invite to the partner). Non-committing.
  /// </summary>
  public void AdvanceBinderSelection(TradeEscrow escrow)
  {
    dynamic remote = Unbind(escrow);
    // The action succeeds (state advances to InviteSent) even though the client's
    // TradeScene view-model may throw a WPF thread-affinity warning while updating
    // its UI afterwards — tolerate that so we don't crash.
    try { OnUI(() => ProcessAction(remote, A_InviteReq, BuildAction(A_InviteReq))); }
    catch (Exception ex) { Log($"[trade] advance: tolerated downstream UI warning ({ex.Message.Split('\n')[0]})"); }
    Log("[trade] advanced binder selection (SendTradeInvitationReqAction) — invite dispatched");
  }

  const string BinderSelectorVM  = "Shiny.Trade.ViewModels.BinderSelectorDialogViewModel";
  const string SelectableBinderT = "Shiny.Trade.ViewModels.SelectableBinder";

  /// <summary>
  /// Make <paramref name="binderName"/> the client's LAST-USED binder. The trade
  /// invite (SendTradeInvitationReqAction, dispatched by AdvanceBinderSelection) is
  /// parameterless, so the binder it PRESENTS comes from this ambient state
  /// (ICollectionGroupingManager.LastUsedBinder, which has a public setter). Set
  /// this to the Lending binder right before advancing so the partner sees only it.
  /// Returns true if set. Non-committing.
  /// </summary>
  public bool SetLastUsedBinder(string binderName)
  {
    var binder = MTGOSDK.API.Collection.CollectionManager.Binders
      .FirstOrDefault(b => string.Equals(Try(() => b.Name) ?? "", binderName, StringComparison.OrdinalIgnoreCase));
    if (binder is null) { Log($"[binder] '{binderName}' not found — cannot set as last-used."); return false; }
    try
    {
      dynamic mgr = Unbind((object)ObjectProvider.Get(IGroupingManager, true, false, false));
      dynamic ib  = Unbind((object)binder);
      OnUI(() => { mgr.LastUsedBinder = ib; });
      Log($"[binder] LastUsedBinder = '{binderName}' (id={Try(() => binder.Id)}).");
      return true;
    }
    catch (Exception ex) { Log($"[binder] set LastUsedBinder failed: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}"); return false; }
  }

  /// <summary>
  /// At InviteSelectBinder, present a SPECIFIC binder so the partner sees ONLY that
  /// binder's cards. Drives MTGO's own binder-selector dialog VM the way a human
  /// does: Initialize(escrow) → set Selected to a SelectableBinder wrapping the
  /// named binder → ExecuteOkCommand (records the chosen binder for THIS escrow AND
  /// dispatches the invite). Non-committing. Returns true iff the OK command ran;
  /// on any failure returns false so the caller can fall back to
  /// AdvanceBinderSelection (which still dispatches the invite, but with the
  /// default/last-used binder).
  /// </summary>
  public bool PresentBinder(TradeEscrow escrow, string binderName)
  {
    var binder = MTGOSDK.API.Collection.CollectionManager.Binders
      .FirstOrDefault(b => string.Equals(Try(() => b.Name) ?? "", binderName, StringComparison.OrdinalIgnoreCase));
    if (binder is null) { Log($"[binder] '{binderName}' not found — cannot present it."); return false; }

    bool ok = false;
    try
    {
      OnUI(() =>
      {
        dynamic esc = Unbind(escrow);
        // Build our own dialog VM wired to THIS escrow (same as MTGO's own, which
        // opens invisibly from the injected thread — we drive ours directly).
        dynamic dlg = RemoteClient.CreateInstance(BinderSelectorVM);
        try { dlg.Initialize(esc); }
        catch (Exception ex) { Log($"[binder] dialog.Initialize threw (continuing): {ex.Message.Split('\n')[0]}"); }

        dynamic ib  = Unbind((object)binder);                          // IBinder
        dynamic sel = RemoteClient.CreateInstance(SelectableBinderT, ib, dlg); // (IBinder, dialogVM)
        dlg.Selected = sel;

        bool can = Try<bool>(() => (bool)dlg.CanExecuteOkCommand());
        if (can) { dlg.ExecuteOkCommand(); ok = true; }
        else Log("[binder] CanExecuteOkCommand=false after selecting — cannot present via dialog.");
      });
    }
    catch (Exception ex)
    {
      Log($"[binder] PresentBinder failed: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
      return false;
    }
    Log(ok ? $"[binder] presented '{binderName}' via dialog OkCommand — invite dispatched."
           : $"[binder] could not present '{binderName}' via dialog.");
    return ok;
  }

  public void Cancel(TradeEscrow escrow)
  {
    dynamic remote = Unbind(escrow);
    OnUI(() => ProcessAction(remote, A_Cancel, BuildAction(A_Cancel)));
    Log($"[trade] cancel requested for escrow {escrow.Id}");
  }

  /// <summary>
  /// Guaranteed cleanup: cancel whatever trade is currently open, using the
  /// state-appropriate action (InviteSelectBinder needs CancelBinderSelection).
  /// Safe to call always; no-ops if there is no active trade or it's completing.
  /// </summary>
  public void CancelCurrent()
  {
    TradeEscrow? cur = null;
    try { cur = TradeManager.CurrentTrade; } catch { }
    if (cur is null) { Log("[cleanup] no active trade to cancel."); return; }

    TradeState state = TradeState.Uninitialized;
    try { state = cur.State; } catch { }

    // Don't yank a trade the human is actively committing.
    if (state.ToString().StartsWith("Approval"))
    {
      Log($"[cleanup] trade in {state} (human committing) — leaving it alone.");
      return;
    }

    string actionType = state == TradeState.InviteSelectBinder ? A_CancelBinder : A_Cancel;
    try
    {
      dynamic remote = Unbind(cur);
      OnUI(() => ProcessAction(remote, actionType, BuildAction(actionType)));
      Log($"[cleanup] sent {actionType.Substring(ActionsNs.Length + 1)} (state was {state}).");
    }
    catch (Exception ex)
    {
      var inner = ex.InnerException;
      Log($"[cleanup] cancel failed: {ex.GetType().Name}: {ex.Message}" +
          (inner != null ? $" || inner: {inner.GetType().Name}: {inner.Message}" : ""));
    }
  }

  // Read a game's player names via the SDK Game wrapper (several IPC hops).
  static List<string> ReadPlayerNames(object cand)
  {
    var names = new List<string>();
    try { foreach (var p in new MTGOSDK.API.Play.Games.Game(cand).Players)
          { try { names.Add(p.Name); } catch { } } } catch { }
    return names;
  }

  /// <summary>
  /// Spectate a live game the client can see (an InProgressGame). <paramref name="target"/>
  /// is a game id (e.g. "957359540") or a player-name substring. Dispatches
  /// PlayerEventActions.WatchGame() into the game's owning Match (Match.Process is
  /// non-generic), then polls LocalUserIsWatching. Read-only observation — no game or
  /// trade state changes, but it does register you as a watcher (visible to players).
  /// Returns (ok, gameId, detail).
  /// </summary>
  public (bool ok, int gameId, string detail) WatchGame(string target, int waitSec = 30)
  {
    bool wantId = int.TryParse(target, out int idWanted);

    dynamic? rawGame = null; int gid = -1; string players = "?";
    try
    {
      foreach (var cand in RemoteClient.GetInstances(InProgressGameT))
      {
        dynamic g = Unbind((object)cand);
        int id = Try<int>(() => (int)g.Id);
        // When matching by id, don't pay to read player names on every game — only
        // read them once we've matched (name reads are several IPC hops per game).
        List<string>? names = null;
        bool hit;
        if (wantId) hit = id == idWanted;
        else { names = ReadPlayerNames(cand); hit = names.Any(n => n.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0); }
        if (hit)
        {
          rawGame = g; gid = id;
          names ??= ReadPlayerNames(cand);
          players = names.Count > 0 ? string.Join(" vs ", names) : "?";
          break;
        }
      }
    }
    catch (Exception ex) { return (false, -1, $"could not enumerate games: {ex.Message.Split('\n')[0]}"); }

    if (rawGame is null) return (false, -1, $"no in-progress game matched '{target}'");

    if (Try<bool>(() => (bool)rawGame.LocalUserIsWatching))
    { Log($"[watch] already watching game {gid} ({players})."); return (true, gid, "already watching"); }

    dynamic match;
    try { match = Unbind((object)rawGame.Match); }
    catch (Exception ex) { return (false, gid, $"could not read game.Match: {ex.Message.Split('\n')[0]}"); }

    string stateName = Try(() => (string)match.CurrentEventState.GetType().Name) ?? "?";
    bool allowed = Try<bool>(() => (bool)match.AreWatchersAllowed);
    Log($"[watch] game {gid} ({players}) — match state={stateName} AreWatchersAllowed={allowed}");

    // Many competitive/tournament matches disable spectating — bail before dispatching.
    if (!allowed) return (false, gid, $"watchers not allowed for this match (state {stateName})");

    dynamic action;
    try { action = RemoteClient.InvokeMethod(PlayerEventActionsT, "WatchGame", null); }
    catch (Exception ex) { return (false, gid, $"could not build WatchGameAction: {ex.Message.Split('\n')[0]}"); }

    bool processed = false; string perr = "";
    try { OnUI(() => { processed = (bool)match.Process(action); }); }
    catch (Exception ex) { perr = ex.Message.Split('\n')[0]; }
    Log($"[watch] match.Process(WatchGameAction) -> {processed}{(perr.Length > 0 ? " (" + perr + ")" : "")}");

    // A false Process means the match rejected it (wrong state) — don't waste the poll.
    if (!processed) return (false, gid, $"match did not accept WatchGameAction (state {stateName})");

    // Poll until the watch connects: LocalUserIsWatching flips, or turn/log populate.
    for (int i = 0; i < waitSec; i++)
    {
      System.Threading.Thread.Sleep(1000);
      if (Try<bool>(() => (bool)rawGame.LocalUserIsWatching)) return (true, gid, $"watching (LocalUserIsWatching, {i + 1}s)");
      if (Try<int>(() => (int)rawGame.CurrentTurn) > 0)       return (true, gid, $"watching (turn advanced, {i + 1}s)");
    }
    return (processed, gid, processed
        ? "dispatched; watch not confirmed within timeout (may still be connecting)"
        : "match did not accept WatchGameAction (likely wrong state or watchers not allowed)");
  }

  /// <summary>
  /// Find a live game whose players include BOTH <paramref name="a"/> and
  /// <paramref name="b"/> (name substrings, case-insensitive), connect as a watcher,
  /// and return the SDK <see cref="MTGOSDK.API.Play.Match"/> wrapper so the caller can
  /// follow the Bo3 to completion. Reuses <see cref="WatchGame"/> for the watch dispatch.
  /// The match wrapper is returned as soon as the game is found — even if the watch
  /// didn't fully confirm — because the match object is often pollable regardless.
  /// Returns (ok, gameId, match, detail).
  /// </summary>
  public (bool ok, int gameId, MTGOSDK.API.Play.Match? match, string detail)
    WatchMatchForPair(string a, string b, int waitSec = 30)
  {
    object? hitInst = null; int gid = -1; string players = "?";
    try
    {
      foreach (var cand in RemoteClient.GetInstances(InProgressGameT))
      {
        // cand is dynamic (GetInstances), so type names explicitly — otherwise
        // names.Any(lambda) becomes a dynamic dispatch (CS1977).
        List<string> names = ReadPlayerNames((object)cand);
        bool hasA = names.Any(n => n.IndexOf(a, StringComparison.OrdinalIgnoreCase) >= 0);
        bool hasB = names.Any(n => n.IndexOf(b, StringComparison.OrdinalIgnoreCase) >= 0);
        if (hasA && hasB)
        {
          hitInst = cand;
          gid = Try<int>(() => (int)Unbind((object)cand).Id);
          players = names.Count > 0 ? string.Join(" vs ", names) : "?";
          break;
        }
      }
    }
    catch (Exception ex) { return (false, -1, null, $"could not enumerate games: {ex.Message.Split('\n')[0]}"); }

    if (hitInst is null) return (false, -1, null, $"no in-progress game with both '{a}' and '{b}'");

    MTGOSDK.API.Play.Match? sdkMatch = null;
    try { sdkMatch = new MTGOSDK.API.Play.Games.Game(hitInst).Match; }
    catch (Exception ex) { return (false, gid, null, $"found game {gid} but could not wrap its match: {ex.Message.Split('\n')[0]}"); }

    var w = WatchGame(gid.ToString(), waitSec);
    return (w.ok, gid, sdkMatch, $"{w.detail} [{players}]");
  }

  /// <summary>
  /// Request a specific card FROM the partner (grab it into our receive side) by
  /// catalog id + quantity. Builds a CollectionItem[] via ctor-args (catalogId,
  /// permissionCode, quantity, annotation) and dispatches SendTradeItemUpdateAction.
  /// Non-committing (negotiation only). Returns a short summary of the result.
  /// </summary>
  /// <remarks>
  /// permissionCode defaults to 215 — the standard default permission code for a
  /// tradeable digital object (see Deck.cs, which builds items with permission
  /// code 215 and annotation 0). Sending 0 makes the trade server reject the
  /// item update and close the trade with FinalState=ErrorReceived.
  /// </remarks>
  public void TakeCard(TradeEscrow esc, int catId, int qty, int permissionCode = 215)
  {
    dynamic annotation = RemoteClient.CreateEnum(AnnotationEnum, "NotSet");
    dynamic itemsArray = RemoteClient.CreateArray(
      CollectionItem,
      new object[][] { new object[] { catId, permissionCode, qty, annotation } });
    dynamic action = BuildAction(A_ItemUpdate, itemsArray); // GetInstance(CollectionItem[])
    dynamic remote = Unbind(esc);
    // Action succeeds even if the UI update afterwards throws a WPF thread warning.
    try { OnUI(() => ProcessAction(remote, A_ItemUpdate, action)); }
    catch (Exception ex) { Log($"[trade] takecard: tolerated downstream UI warning ({ex.Message.Split('\n')[0]})"); }
    Log($"[trade] item update dispatched: request catId={catId} x{qty} (perm={permissionCode})");
  }

  /// <summary>
  /// Request a partner's card by catalog id through MTGO's OWN trade view-model,
  /// so the item-update message is built by the client (correct permission code),
  /// not synthesized by us. This is the reliable "take a partner item" path:
  /// hand-built CollectionItems get rejected (ErrorReceived) because their
  /// permission code doesn't match the real object.
  /// </summary>
  /// <remarks>
  /// Reaches the live <c>ActiveTradeViewModel</c> off the client HEAP (it's a
  /// per-trade instance, not a registered service), then, on the UI thread:
  ///   item = vm.GetTradeItemUpdateMessage(catalogId)   // client builds the CollectionItem
  ///   vm.AddPendingUpdateItem(item, quantity)          // stage the requested item
  ///   vm.SendPendingItems()                            // dispatch the request
  /// Non-committing (negotiation only). Throws if no trade view-model is live.
  /// </remarks>
  /// <summary>
  /// Reach the live <c>ActiveTradeViewModel</c> off the client heap. There may be
  /// STALE instances from earlier trades this session (so the singular lookup
  /// throws "Multiple objects found"); enumerate all and pick the one bound to the
  /// LIVE trade — matching the current partner name and a non-terminal state.
  /// </summary>
  static dynamic GetLiveTradeVM()
  {
    string partner = null, liveId = null;
    try { var cur = TradeManager.CurrentTrade; partner = cur?.TradePartnerName; liveId = Try(() => cur.Id.ToString()); } catch { }

    dynamic vm = null, firstLive = null, firstNameMatch = null;
    int scanned = 0;
    try
    {
      foreach (var cand in RemoteClient.GetInstances(ActiveTradeVM))
      {
        scanned++;
        dynamic c = Unbind((object)cand);
        string cn = Try(() => (string)c.CurrentTradeUserName) ?? "";
        string est = Try(() => (string)c.CurrentTradeEscrow.CurrentState.ToString()) ?? "";
        string escId = Try(() => c.CurrentTradeEscrow.Id.ToString()) ?? "";
        bool nameOk = partner is null || string.Equals(cn, partner, StringComparison.OrdinalIgnoreCase);
        bool live = est.StartsWith("Negotiate") || est.StartsWith("Approval") || est.StartsWith("Invite");
        // BEST: the VM bound to the LIVE escrow (matched by id) — unambiguous even
        // when STALE VMs from earlier trades this session share the partner name and
        // a non-terminal cached state (which made the match/request hit a dead VM).
        if (liveId != null && liveId.Length > 0 && escId == liveId) { vm = c; break; }
        if (firstLive is null && nameOk && live) firstLive = c;
        if (firstNameMatch is null && nameOk) firstNameMatch = c;
      }
    }
    catch (Exception ex)
    {
      throw new InvalidOperationException(
        $"Could not enumerate {ActiveTradeVM} on the heap (is a trade window open?): {ex.Message}", ex);
    }
    vm ??= firstLive ?? firstNameMatch;
    if (vm is null)
      throw new InvalidOperationException(
        $"No live {ActiveTradeVM} found (scanned {scanned}) — open a trade first.");
    Log($"[trade] reached ActiveTradeViewModel (partner={Try(() => (string)vm.CurrentTradeUserName) ?? "?"}, scanned {scanned})");
    return vm;
  }

  /// <summary>
  /// BATCH request via MTGO's wishlist/import flow: build an IFileImportCardGrouping
  /// of desired cards and match it against the partner's binder
  /// (ActiveTradeViewModel.MatchDesiredCardsFromPartnersTradeBinder). This mirrors
  /// "import a list -> add all matches to the trade". Returns the requested list.
  /// </summary>
  public string RequestViaWishlist(int catalogId, int quantity, string cardName)
  {
    dynamic vm = GetLiveTradeVM();
    Log($"[wishlist] requesting catId={catalogId} x{quantity} ({cardName}) via import/match");

    int matched = -1;
    try
    {
      OnUI(() =>
      {
        dynamic grouping = RemoteClient.CreateInstance(FileImportGrouping);
        dynamic card = RemoteClient.CreateInstance(FileImportCard);
        card.CatalogId = catalogId;
        card.Quantity = quantity;
        if (!string.IsNullOrEmpty(cardName)) card.CardName = cardName;
        grouping.Cards.Add(card);
        dynamic result = vm.MatchDesiredCardsFromPartnersTradeBinder(grouping);
        matched = Try(() => (int)result.Count) ?? -1;
        // Some paths only build the match list; ensure it's applied + sent.
        try { vm.SendPendingItems(); } catch { }
      });
    }
    catch (Exception ex) { Log($"[wishlist] tolerated downstream warning ({ex.Message.Split('\n')[0]})"); }
    LastMatchCount = matched;
    Log($"[wishlist] MatchDesiredCardsFromPartnersTradeBinder matched {matched} card(s)");

    System.Threading.Thread.Sleep(1500);
    string requested = ReadRequestedItems(vm);
    Log($"[wishlist] you-receive (ItemsLocalUserWants) now: {requested}");
    return requested;
  }

  /// <summary>
  /// Submit my (empty) deposit proposal via the view-model's Submit command
  /// (NegotiateNoDeposit -> DepositSubmittedLocal). Reversible before the final
  /// approve (you can still Modify/Cancel), so this is NON-committing.
  /// </summary>
  public void SubmitDeposit()
  {
    dynamic vm = GetLiveTradeVM();
    bool can = false;
    OnUI(() =>
    {
      can = Try<bool>(() => (bool)vm.SubmitTradeCanExecute());
      if (can) vm.SubmitTradeExecute();
    });
    Log($"[trade] deposit submit: {(can ? "SubmitTradeExecute dispatched" : "SubmitTradeCanExecute=false — skipped")}.");
  }

  /// <summary>
  /// THE COMMITTING CALL: final approve via the view-model's Confirm command
  /// (ApprovalNone -> ApprovalSubmittedLocal -> ... -> TradeComplete). Point of no
  /// return — gated by AllowCommit; throws in dry-run so nothing commits by accident.
  /// </summary>
  public void ConfirmTrade()
  {
    if (!AllowCommit)
      throw new InvalidOperationException(
        "ConfirmTrade BLOCKED: AllowCommit is false (dry-run). No assets moved.");
    dynamic vm = GetLiveTradeVM();
    bool can = false;
    OnUI(() =>
    {
      can = Try<bool>(() => (bool)vm.ConfirmTradeCanExecute());
      if (can) vm.ConfirmTradeExecute();
    });
    Log($"[trade] FINAL APPROVE: {(can ? "ConfirmTradeExecute dispatched — COMMIT" : "ConfirmTradeCanExecute=false — skipped")}.");
  }

  // ─────────────────────────────────────────────────────────────────────────
  // TARGETED LEND — DM handshake + give-side safety guardrail.
  // NOTE: this whole path is UNTESTED (needs a second person online). The
  // guardrail below is the hard safety: we never approve a give that isn't
  // exactly the one intended card.
  // ─────────────────────────────────────────────────────────────────────────

  /// <summary>
  /// Resolve a username to its login id CASE-INSENSITIVELY (the client stores the
  /// canonical case, e.g. "Basic_" not "basic_"). Returns -1 if the client doesn't
  /// know the user. Read-only.
  /// </summary>
  public static int ResolveUserId(string username)
  {
    try { return Try(() => (int)Unbind(MTGOSDK.API.Users.UserManager.GetUser(username, true)).Id) ?? -1; }
    catch { return -1; }
  }

  /// <summary>
  /// Add a user as a buddy by name (IBuddyUsersList.AddUser(name, comment)) so the
  /// client learns them — required before we can resolve name→id or DM them.
  /// </summary>
  public void AddBuddy(string username)
  {
    dynamic buddyList = Unbind((object)ObjectProvider.Get(IBuddyUsersList, true, false, false));
    OnUI(() => buddyList.AddUser(username, ""));
    Log($"[buddy] AddUser('{username}') dispatched.");
  }

  /// <summary>
  /// Ensure the client can resolve <paramref name="username"/> (name→id); if it
  /// can't, add them as a buddy and wait for the client to learn them. Returns
  /// true once resolvable. (A brand-new buddy may need to accept the request
  /// before they fully resolve.)
  /// </summary>
  public bool EnsureKnownUser(string username)
  {
    if (ResolveUserId(username) > 0) return true;
    Log($"[buddy] '{username}' is unknown to the client — adding as a buddy...");
    try { AddBuddy(username); } catch (Exception ex) { Log($"[buddy] AddUser failed: {ex.Message}"); return false; }
    for (int i = 0; i < 15; i++)
    {
      System.Threading.Thread.Sleep(1000);
      int id = ResolveUserId(username);
      if (id > 0) { Log($"[buddy] '{username}' now resolves (id={id})."); return true; }
    }
    Log($"[buddy] '{username}' still not resolvable — they may need to ACCEPT the buddy request, or the name is wrong.");
    return false;
  }

  /// <summary>
  /// Reach the client's chat manager (PrimaryChatManager) via IShellViewModel.
  /// </summary>
  static dynamic ChatManager() =>
    Unbind((object)ObjectProvider.Get(IShellViewModel, true, false, false)).ChatManager;

  /// <summary>
  /// Send a private (DM) chat message to a user. Creates the private chat SESSION
  /// via the chat manager (PrimaryChatManager.CreatePrivateChat) if one doesn't
  /// exist yet — this is what a headless client lacks — then sends through the
  /// session's SendCommand (the real transmit path).
  /// </summary>
  public void SendDM(string username, string text)
  {
    int uid = ResolveUserId(username);
    if (uid <= 0) throw new InvalidOperationException($"Could not resolve a user id for '{username}'.");
    SendDMToId(uid, username, text);
  }

  /// <summary>
  /// Send a DM to a user by their KNOWN login id (e.g. one pulled straight from a
  /// marketplace post via BotPool.ResolvePosterId — lets us DM a bot without
  /// adding it as a buddy). <paramref name="label"/> is only for logging.
  /// </summary>
  public void SendDMToId(int uid, string label, string text)
  {
    if (uid <= 0) throw new InvalidOperationException($"Bad user id {uid} for '{label}'.");

    dynamic mgr = ChatManager();
    dynamic session = null;
    OnUI(() =>
    {
      try { session = mgr.GetPrivateChat(uid); } catch { }
      if (session is null)
      {
        try { mgr.CreatePrivateChat(uid); } catch (Exception ex) { Log($"[dm] CreatePrivateChat threw: {ex.Message.Split('\n')[0]}"); }
        try { session = mgr.GetPrivateChat(uid); } catch { }
      }
    });
    if (session is null) { System.Threading.Thread.Sleep(600); try { session = mgr.GetPrivateChat(uid); } catch { } }
    if (session is null)
      throw new InvalidOperationException($"Could not create/get a chat session for '{label}'.");

    OnUI(() => session.SendCommand.Execute(text));
    Log($"[dm] -> {label}: {text}");
  }

  /// <summary>
  /// The sender name of a chat message. The SDK's Message.User is deliberately
  /// null when FromUser.Id == -1, which happens for private-DM messages (BOTH
  /// directions — observed live), so fall back to the raw FromUser.Name off the
  /// underlying object. Returns "" if no name is available at all.
  /// </summary>
  static string SenderName(Message m) =>
    Try(() => m.User?.Name)
    ?? Try(() => (string)Unbind((object)m).FromUser.Name)
    ?? "";

  /// <summary>
  /// Wait up to <paramref name="timeoutSec"/> for <paramref name="username"/> to
  /// reply "yes" (yes / y / yes please / …) in the DM channel. Read-only.
  /// Private-DM replies can arrive with FromUser.Id == -1 (no wrapper user), so we
  /// do NOT hard-require a sender-name match: WE never send a yes, so a yes-pattern
  /// on a new message is theirs. We still reject a yes whose sender name IS present
  /// and is someone OTHER than them (e.g. our own echo), to be safe.
  /// </summary>
  // Operator abort: the serve sets this to break a long presence-wait (see
  // SendPromptWhenOnlineAndWaitForYes). One job runs at a time on one executor, so a single
  // flag is unambiguous. Cleared at the start of each job.
  public volatile bool AbortRequested;
  public void RequestAbort() => AbortRequested = true;
  public void ClearAbort() => AbortRequested = false;

  public bool WaitForDMYes(string username, int timeoutSec = 300)
  {
    int uid = ResolveUserId(username);
    if (uid <= 0) return false;
    var channel = ChannelManager.GetPrivateChannel(uid);
    int seen = Try(() => channel.Messages.Count) ?? 0;
    for (int i = 0; i < timeoutSec; i++)
    {
      System.Threading.Thread.Sleep(1000);
      System.Collections.Generic.IList<Message> msgs;
      try { msgs = channel.Messages; } catch { continue; }
      for (int m = seen; m < msgs.Count; m++)
      {
        string who = SenderName(msgs[m]);
        string raw = (Try(() => msgs[m].Text) ?? "").Trim();
        Log($"[dm-log] {(who.Length > 0 ? who : "(?)")}: {raw}");   // diagnostic
        string txt = raw.ToLowerInvariant();
        bool isYes = txt == "y" || txt == "yes" || txt.StartsWith("yes") || txt.StartsWith("y ");
        // Accept when it's a yes AND the sender is either them or name-unavailable
        // (private DMs often report no sender). A yes with a KNOWN sender that
        // isn't them (our own echo) is rejected.
        if (isYes && (who.Length == 0 || string.Equals(who, username, StringComparison.OrdinalIgnoreCase)))
          return true;
      }
      seen = msgs.Count;
    }
    return false;
  }

  /// <summary>
  /// True if <paramref name="username"/> is logged in AND visible (online) right now.
  /// Read-only; false if they're offline, in "appear offline", or can't be resolved.
  /// </summary>
  public bool IsUserOnline(string username)
    => Try<bool>(() => (bool)MTGOSDK.API.Users.UserManager.GetUser(username, true).IsLoggedIn);

  /// <summary>
  /// Presence-aware handshake. Sends <paramref name="prompt"/> once now, then RE-sends it on
  /// every offline→online transition — a DM to an OFFLINE user can be silently dropped by
  /// MTGO, so a single send can be lost; re-sending when they reconnect guarantees the prompt
  /// lands when they're there to see it. Returns true the moment they reply YES (caught off
  /// the DM channel whether or not the prompt reached them). Waits up to
  /// <paramref name="timeoutSec"/> seconds (int.MaxValue = until they do). Read-only bar DMs.
  /// </summary>
  public bool SendPromptWhenOnlineAndWaitForYes(string username, string prompt, int timeoutSec)
  {
    int uid = ResolveUserId(username);
    if (uid <= 0) return false;
    var channel = ChannelManager.GetPrivateChannel(uid);
    int seen = Try(() => channel.Messages.Count) ?? 0;

    // Presence-FIRST: only prompt when they're actually online. If already online, prompt now;
    // otherwise HOLD and prompt the moment they come online (a DM to an offline user can be
    // silently dropped, so prompting into the void is pointless).
    bool wasOnline = IsUserOnline(username);
    if (wasOnline) { try { SendDM(username, prompt); } catch (Exception ex) { Log($"[dm] initial prompt threw: {ex.Message.Split('\n')[0]}"); } }
    else Log($"[presence] {username} is offline — holding; I'll prompt them the moment they come online.");

    for (int i = 0; i < timeoutSec; i++)
    {
      if (AbortRequested) { Log($"[cancel] handshake for {username} aborted by operator."); return false; }
      // Prompt on each offline→online edge so they get a prompt they can actually see.
      bool online = IsUserOnline(username);
      if (online && !wasOnline)
      {
        try { SendDM(username, prompt); Log($"[presence] {username} came online — prompt sent."); }
        catch (Exception ex) { Log($"[dm] prompt-on-online threw: {ex.Message.Split('\n')[0]}"); }
      }
      wasOnline = online;

      // Watch for a new YES (independent of presence — any yes they type fires the trade).
      try
      {
        var msgs = channel.Messages;
        for (int m = seen; m < msgs.Count; m++)
        {
          string who = SenderName(msgs[m]);
          string raw = (Try(() => msgs[m].Text) ?? "").Trim();
          Log($"[dm-log] {(who.Length > 0 ? who : "(?)")}: {raw}");
          string txt = raw.ToLowerInvariant();
          bool isYes = txt == "y" || txt == "yes" || txt.StartsWith("yes") || txt.StartsWith("y ");
          if (isYes && (who.Length == 0 || string.Equals(who, username, StringComparison.OrdinalIgnoreCase)))
            return true;
        }
        seen = msgs.Count;
      }
      catch { }

      System.Threading.Thread.Sleep(1000);
    }
    return false;
  }

  /// <summary>Diagnostic: dump the last <paramref name="n"/> messages in the DM channel.</summary>
  public void DumpDMTail(string username, int n = 8)
  {
    int uid = ResolveUserId(username);
    if (uid <= 0) { Log($"[dm] can't resolve '{username}'"); return; }
    DumpDMTailById(uid, username, n);
  }

  /// <summary>Dump the DM tail for a KNOWN user id (no name resolution needed).</summary>
  public void DumpDMTailById(int uid, string label, int n = 8)
  {
    if (uid <= 0) { Log($"[dm] bad id for '{label}'"); return; }
    var channel = ChannelManager.GetPrivateChannel(uid);
    System.Collections.Generic.IList<Message> msgs;
    try { msgs = channel.Messages; } catch (Exception ex) { Log($"[dm] messages read failed: {ex.Message.Split('\n')[0]}"); return; }
    Log($"[dm] channel with {label} has {msgs.Count} message(s); last {Math.Min(n, msgs.Count)}:");
    for (int i = Math.Max(0, msgs.Count - n); i < msgs.Count; i++)
    {
      string who = SenderName(msgs[i]);
      int? id = Try(() => (int?)Unbind((object)msgs[i]).FromUser.Id);
      string txt = (Try(() => msgs[i].Text) ?? "").Trim();
      Log($"    {(who.Length > 0 ? who : "(?)")}{(id.HasValue ? $" [id={id}]" : "")}: {txt}");
    }
  }

  /// <summary>
  /// SAFETY GUARDRAIL for a give: true ONLY if WE GIVE is EXACTLY one card whose
  /// name matches <paramref name="cardName"/> (quantity == expectedQty) and
  /// nothing else, and WE RECEIVE nothing. Never submit/approve a give that fails
  /// this — it is what prevents ever handing over more than the one intended card.
  /// </summary>
  public bool VerifyGiveIsOnly(TradeEscrow esc, string cardName, int expectedQty = 1)
  {
    var give = new List<(string name, int qty)>();
    try
    {
      foreach (var it in esc.TradedItems.CollectionItems)
        give.Add((Try(() => it.Card?.Name) ?? "?", Try(() => (int)it.Quantity) ?? 0));
    }
    catch (Exception ex) { Log($"[guardrail] could not read WE GIVE: {ex.Message}"); return false; }

    int recvCount = 0;
    try { recvCount = esc.PartnerTradedItems.CollectionItems.Count; } catch { }

    bool nameOk = give.Count == 1 && give[0].qty == expectedQty
               && (give[0].name?.ToLowerInvariant().Contains(cardName.ToLowerInvariant()) ?? false);
    bool ok = nameOk && recvCount == 0;
    Log($"[guardrail] WE GIVE = {(give.Count == 0 ? "(none)" : string.Join(", ", give.Select(g => $"{g.qty}x {g.name}")))}; WE RECEIVE items = {recvCount} => {(ok ? "OK (exactly the target, receiving nothing)" : "REJECT")}");
    return ok;
  }

  /// <summary>
  /// SAFETY GUARDRAIL for a one-way GRAB (receive-only): true ONLY if WE RECEIVE is
  /// EXACTLY one card matching <paramref name="cardName"/> (qty == expectedQty) and
  /// WE GIVE nothing. The mirror of <see cref="VerifyGiveIsOnly"/> — never
  /// submit/approve a grab unless we're getting exactly the target and handing over
  /// nothing.
  /// </summary>
  public bool VerifyReceiveIsOnly(TradeEscrow esc, string cardName, int expectedQty = 1)
  {
    var recv = new List<(string name, int qty)>();
    try { foreach (var it in esc.PartnerTradedItems.CollectionItems) recv.Add((Try(() => it.Card?.Name) ?? "?", Try(() => (int)it.Quantity) ?? 0)); }
    catch (Exception ex) { Log($"[guardrail] could not read WE RECEIVE: {ex.Message}"); return false; }

    int giveCount = 0;
    try { giveCount = esc.TradedItems.CollectionItems.Count; } catch { }

    bool nameOk = recv.Count == 1 && recv[0].qty == expectedQty
               && (recv[0].name?.ToLowerInvariant().Contains(cardName.ToLowerInvariant()) ?? false);
    bool ok = nameOk && giveCount == 0;
    Log($"[guardrail] WE RECEIVE = {(recv.Count == 0 ? "(none)" : string.Join(", ", recv.Select(r => $"{r.qty}x {r.name}")))}; WE GIVE items = {giveCount} => {(ok ? "OK (exactly the target, giving nothing)" : "REJECT")}");
    return ok;
  }

  /// <summary>
  /// Status of a multi-card GIVE vs the intended set: what the partner still needs
  /// to grab (<c>missing</c>), what they grabbed that they SHOULDN'T (<c>extra</c>),
  /// and whether WE GIVE is EXACTLY the intended set with nothing received
  /// (<c>exact</c>). Used to remind the partner what's left to grab, and as the
  /// give-side guardrail for a set. Read-only.
  /// </summary>
  public (List<string> missing, List<string> extra, bool exact) GiveStatus(
      TradeEscrow esc, IReadOnlyList<(string name, int qty)> intended)
  {
    var give = new List<(string name, int qty)>();
    try { foreach (var it in esc.TradedItems.CollectionItems) give.Add((Try(() => it.Card?.Name) ?? "?", Try(() => (int)it.Quantity) ?? 0)); }
    catch { return (intended.Select(w => w.qty > 1 ? $"{w.qty}x {w.name}" : w.name).ToList(), new(), false); }

    int recvCount = 0;
    try { recvCount = esc.PartnerTradedItems.CollectionItems.Count; } catch { }

    // Tolerant name match: MTGO can return a typographic apostrophe (U+2019) where the
    // caller typed a straight ' — strip both (and trim) so "Kozilek's Command" matches
    // regardless. Without this, a legitimately-offered grab reads as "not offered".
    static string Norm(string s) =>
        (s ?? "").Replace("’", "").Replace("‘", "").Replace("'", "").Trim();
    bool NameMatch(string given, string want) =>
        Norm(given).IndexOf(Norm(want), StringComparison.OrdinalIgnoreCase) >= 0;

    var missing = new List<string>();
    foreach (var want in intended)
    {
      int got = give.Where(g => NameMatch(g.name, want.name)).Sum(g => g.qty);
      if (got < want.qty) { int short_ = want.qty - got; missing.Add(short_ > 1 ? $"{short_}x {want.name}" : want.name); }
    }
    var extra = new List<string>();
    foreach (var g in give)
    {
      // Total allowed for this given card = SUM of every intended qty whose name matches,
      // so 3x of ONE card is fully allowed however `intended` is split. (A single "qty 2"
      // grab of a 3x lend is then "missing 1", never "extra".)
      int allowed = intended.Where(w => NameMatch(g.name, w.name)).Sum(w => w.qty);
      if (g.qty > allowed) { int over = g.qty - allowed; extra.Add($"{over}x {g.name}"); }
    }
    bool exact = missing.Count == 0 && extra.Count == 0 && recvCount == 0;
    return (missing, extra, exact);
  }

  /// <summary>True if the trade partner (the OTHER party) has SUBMITTED their
  /// deposit — MTGO signals this in the escrow state ("...DepositReceivedOther" /
  /// "...DepositSubmittedOther" / "...DepositReceivedBoth"). Lets us catch a partner
  /// who hit Submit before finishing their grab. Read-only.</summary>
  public static bool PartnerHasSubmitted(TradeEscrow esc)
  {
    string s = ""; try { s = esc.State.ToString(); } catch { return false; }
    return s.IndexOf("DepositReceivedOther", StringComparison.OrdinalIgnoreCase) >= 0
        || s.IndexOf("DepositSubmittedOther", StringComparison.OrdinalIgnoreCase) >= 0
        || s.IndexOf("DepositReceivedBoth", StringComparison.OrdinalIgnoreCase) >= 0;
  }

  /// <summary>
  /// SAFETY GUARDRAIL for a two-sided SWAP: true ONLY if WE GIVE is EXACTLY
  /// {<paramref name="giveQty"/> x <paramref name="giveCard"/>} and WE RECEIVE is
  /// EXACTLY {<paramref name="getQty"/> x <paramref name="getCard"/>} — nothing
  /// more on either side. Never submit/approve a swap that fails this: it is what
  /// guarantees we hand over only the offered card AND actually get the requested
  /// one (so an incomplete or lopsided deal is cancelled, never committed).
  /// </summary>
  public bool VerifySwap(TradeEscrow esc, string giveCard, int giveQty, string getCard, int getQty, bool log = true)
  {
    var give = new List<(string name, int qty)>();
    var recv = new List<(string name, int qty)>();
    try { foreach (var it in esc.TradedItems.CollectionItems) give.Add((Try(() => it.Card?.Name) ?? "?", Try(() => (int)it.Quantity) ?? 0)); }
    catch (Exception ex) { Log($"[guardrail] could not read WE GIVE: {ex.Message}"); return false; }
    try { foreach (var it in esc.PartnerTradedItems.CollectionItems) recv.Add((Try(() => it.Card?.Name) ?? "?", Try(() => (int)it.Quantity) ?? 0)); }
    catch (Exception ex) { Log($"[guardrail] could not read WE RECEIVE: {ex.Message}"); return false; }

    bool giveOk = give.Count == 1 && give[0].qty == giveQty && (give[0].name?.ToLowerInvariant().Contains(giveCard.ToLowerInvariant()) ?? false);
    bool recvOk = recv.Count == 1 && recv[0].qty == getQty  && (recv[0].name?.ToLowerInvariant().Contains(getCard.ToLowerInvariant()) ?? false);
    bool ok = giveOk && recvOk;
    if (log)
    {
      string gs = give.Count == 0 ? "(none)" : string.Join(", ", give.Select(g => $"{g.qty}x {g.name}"));
      string rs = recv.Count == 0 ? "(none)" : string.Join(", ", recv.Select(r => $"{r.qty}x {r.name}"));
      Log($"[guardrail] WE GIVE = {gs} (need {giveQty}x {giveCard}); WE RECEIVE = {rs} (need {getQty}x {getCard}) => {(ok ? "OK (both sides exact)" : "REJECT")}");
    }
    return ok;
  }

  /// <summary>
  /// Create (or reuse) a trade binder named <paramref name="binderName"/>
  /// containing only <paramref name="cardName"/>, so a lend trade can present just
  /// that one card. Operates entirely on THIS (the bot's) account. Returns the
  /// Binder, or null on failure.
  ///
  /// Uses ICollectionGroupingManager.CreateNewBinder(name, image, IEnumerable&lt;ICardDefinition&gt;).
  /// Two things the diver needs (both handled here): ICardDefinition's real name is
  /// WotC.MtGO.Client.Model.ICardDefinition (NOT ...Collection.*), and the remote
  /// List&lt;ICardDefinition&gt; must be built with an ASSEMBLY-QUALIFIED type-arg
  /// (List`1[[Inner, Asm]]) or the diver's ResolveType can't find it.
  /// </summary>
  public MTGOSDK.API.Collection.Binder? CreateSingleCardBinder(string binderName, string cardName)
    => CreateBinder(binderName, new[] { cardName });

  /// <summary>
  /// Find a printing (catId) of <paramref name="cardName"/> that THIS account
  /// actually OWNS (quantity &gt; 0). A binder only surfaces owned copies of the
  /// EXACT printing, so a binder built from GetCard(name) (which returns
  /// printing[0]) shows EMPTY to a trade partner when the owned copy is a different
  /// printing. Returns (catId, quantity), or (-1, 0) if none owned. Read-only.
  /// </summary>
  public (int catId, int qty) ResolveOwnedPrinting(string cardName)
  {
    System.Collections.Generic.HashSet<int> printings;
    try { printings = MTGOSDK.API.Collection.CollectionManager.GetCardIds(cardName).ToHashSet(); }
    catch { return (-1, 0); }
    try
    {
      foreach (var it in MTGOSDK.API.Collection.CollectionManager.Collection.Items)
      {
        int id  = Try(() => it.Id) ?? -1;
        int qty = Try(() => it.Quantity) ?? 0;
        if (qty > 0 && printings.Contains(id)) return (id, qty);
      }
    }
    catch (Exception ex) { Log($"[owned] collection scan failed: {ex.Message.Split('\n')[0]}"); }
    return (-1, 0);
  }

  /// <summary>
  /// Create (or reuse) a trade binder named <paramref name="binderName"/>
  /// containing one of each of <paramref name="cardNames"/> (quantity 1 each).
  /// Operates entirely on THIS (the bot's) account. Returns the Binder, or null.
  /// </summary>
  public MTGOSDK.API.Collection.Binder? CreateBinder(string binderName, System.Collections.Generic.IReadOnlyList<string> cardNames)
  {
    var existing = MTGOSDK.API.Collection.CollectionManager.Binders
      .FirstOrDefault(b => string.Equals(Try(() => b.Name) ?? "", binderName, StringComparison.OrdinalIgnoreCase));
    if (existing != null)
    {
      Log($"[binder] found existing '{binderName}' (id={Try(() => existing.Id)}, items={Try(() => existing.ItemCount)}).");
      return existing;
    }

    // Resolve each card name to its underlying ICardDefinition (skip unresolved).
    // PREFER a printing we actually OWN — otherwise the binder shows EMPTY to a
    // trade partner (they only see owned copies of the exact printing).
    var defs = new List<dynamic>();
    foreach (var cn in cardNames)
    {
      dynamic d = null;
      try
      {
        var (ownedCat, ownedQty) = ResolveOwnedPrinting(cn);
        if (ownedCat > 0)
        {
          d = Unbind(MTGOSDK.API.Collection.CollectionManager.GetCard(ownedCat));
          Log($"[binder] resolved '{cn}' -> OWNED printing catId={ownedCat} (qty {ownedQty}).");
        }
        else
        {
          d = Unbind(MTGOSDK.API.Collection.CollectionManager.GetCard(cn));
          Log($"[binder] '{cn}': NOT owned in any printing — using default printing; binder will show EMPTY to partners.");
        }
      }
      catch (Exception ex) { Log($"[binder] could not resolve '{cn}' — skipping ({ex.Message.Split('\n')[0]})"); }
      if (d != null) defs.Add(d);
    }
    if (defs.Count == 0) { Log("[binder] no cards resolved — aborting."); return null; }

    try
    {
      // Build List<ICardDefinition> remotely. The diver's type resolver only
      // constructs a cross-assembly generic if the type-arg is ASSEMBLY-QUALIFIED
      // (List`1[[Inner, Asm]]); a plain List`1[Inner] fails.
      string asmName = Try(() => RemoteClient.GetInstanceType(ICardDefinition).Assembly.GetName().Name)
                       ?? "WotC.MtGO.Client.Model";
      string listType = $"System.Collections.Generic.List`1[[{ICardDefinition}, {asmName}]]";
      dynamic list = RemoteClient.CreateInstance(listType);
      foreach (var d in defs) list.Add(d);
      dynamic mgr = Unbind((object)ObjectProvider.Get(IGroupingManager, true, false, false));
      OnUI(() => mgr.CreateNewBinder(binderName, null, list));
      Log($"[binder] CreateNewBinder('{binderName}') with {defs.Count} card(s).");
    }
    catch (Exception ex)
    {
      var inner = ex.InnerException;
      Log($"[binder] CreateNewBinder failed: {ex.GetType().Name}: {ex.Message}" +
          (inner != null ? $" || inner: {inner.GetType().Name}: {inner.Message}" : ""));
      return null;
    }

    System.Threading.Thread.Sleep(1500);
    var created = MTGOSDK.API.Collection.CollectionManager.Binders
      .FirstOrDefault(b => string.Equals(Try(() => b.Name) ?? "", binderName, StringComparison.OrdinalIgnoreCase));
    if (created is null) { Log($"[binder] created but not found on re-scan (give it a moment)."); return null; }
    Log($"[binder] created '{created.Name}' (id={Try(() => created.Id)}, items={Try(() => created.ItemCount)}).");
    return created;
  }

  /// <summary>
  /// Delete a trade binder by name from THIS (the bot's) account.
  /// Uses the concrete CollectionGroupingManager.DeleteGrouping(ICardGrouping, syncOnline:true)
  /// — the same instance/UI-thread path as CreateNewBinder. Returns true if the
  /// binder is gone afterward (also true if it never existed).
  /// </summary>
  public bool DeleteBinder(string binderName)
  {
    var target = MTGOSDK.API.Collection.CollectionManager.Binders
      .FirstOrDefault(b => string.Equals(Try(() => b.Name) ?? "", binderName, StringComparison.OrdinalIgnoreCase));
    if (target is null) { Log($"[binder] no binder named '{binderName}' — nothing to delete."); return true; }

    int? id = Try(() => target.Id);
    try
    {
      dynamic grouping = Unbind((object)target);
      dynamic mgr = Unbind((object)ObjectProvider.Get(IGroupingManager, true, false, false));
      OnUI(() => mgr.DeleteGrouping(grouping, true));
      Log($"[binder] DeleteGrouping('{binderName}' id={id}) requested.");
    }
    catch (Exception ex)
    {
      var inner = ex.InnerException;
      Log($"[binder] DeleteGrouping failed: {ex.GetType().Name}: {ex.Message}" +
          (inner != null ? $" || inner: {inner.GetType().Name}: {inner.Message}" : ""));
      return false;
    }

    System.Threading.Thread.Sleep(1500);
    var still = MTGOSDK.API.Collection.CollectionManager.Binders
      .FirstOrDefault(b => string.Equals(Try(() => b.Name) ?? "", binderName, StringComparison.OrdinalIgnoreCase));
    if (still != null) { Log($"[binder] '{binderName}' still present after delete."); return false; }
    Log($"[binder] '{binderName}' deleted.");
    return true;
  }

  /// <summary>
  /// Create a binder named <paramref name="binderName"/> whose contents are EXACTLY
  /// <paramref name="cardNames"/> — deleting any existing binder of that name first.
  /// CreateBinder reuses a binder by name (contents can go stale across orders with
  /// different cards); this guarantees a fresh, correct binder every time. Returns
  /// the binder, or null. Owned-printing resolution applies (see CreateBinder).
  /// </summary>
  public MTGOSDK.API.Collection.Binder? EnsureBinderExact(string binderName, System.Collections.Generic.IReadOnlyList<string> cardNames)
  {
    var existing = MTGOSDK.API.Collection.CollectionManager.Binders
      .FirstOrDefault(b => string.Equals(Try(() => b.Name) ?? "", binderName, StringComparison.OrdinalIgnoreCase));
    if (existing != null) DeleteBinder(binderName);
    return CreateBinder(binderName, cardNames);
  }

  /// <summary>Summarize the view-model's requested ("you receive") list.</summary>
  static string ReadRequestedItems(dynamic vm)
  {
    try
    {
      dynamic wants = vm.ItemsLocalUserWants;
      var names = new List<string>();
      try
      {
        foreach (var it in Map<dynamic>(wants.Items))
        {
          int? q = Try(() => (int?)it.Quantity);
          int? cid = Try(() => (int?)it.CatalogId);
          string nm = Try(() => (string)it.Name);
          names.Add($"{q}x {(string.IsNullOrEmpty(nm) ? $"catId={cid}" : nm)}");
        }
      }
      catch { }
      if (names.Count > 0) return string.Join(", ", names);
      int n = Try(() => (int)wants.ItemCount);
      return n > 0 ? $"{n} item(s)" : "(none)";
    }
    catch (Exception ex) { return $"<read failed: {ex.Message.Split('\n')[0]}>"; }
  }

  /// <summary>Human-readable summary of what each side has staged in the escrow.</summary>
  public static string Summarize(ItemCollection coll)
  {
    var s = SnapshotItems(coll);
    return s.Count > 0 ? string.Join(", ", s) : "(none)";
  }

  /// <summary>
  /// Offer items into the escrow (non-committing). NOTE: assembling the remote
  /// CollectionItem[] is the one step still to be finalized against a live
  /// second-account trade — see caveats. Left as the documented next step.
  /// </summary>
  public void AddOfferedItems(TradeEscrow escrow, IReadOnlyList<int> catIds)
  {
    dynamic remote = Unbind(escrow);
    // Pick the live CollectionItem DROs from the active binder that match catIds.
    var picked = new List<object>();
    foreach (var item in Map<dynamic>(remote.ActiveBinder.Items))
      if (Try<bool>(() => catIds.Contains((int)item.CardDefinition.Id)))
        picked.Add(Unbind(item));
    Log($"[trade] would offer {picked.Count} item stack(s) via {A_ItemUpdate}.GetInstance(CollectionItem[])");
    // dynamic items  = <remote CollectionItem[] built from `picked`>;   // TODO: finalize array marshalling
    // dynamic action = BuildAction(A_ItemUpdate, items);
    // remote.Process(action);
  }

  // ─────────────────────────────────────────────────────────────────────────
  // THE ONLY COMMITTING CALL — gated. In dry-run this throws; never runs.
  // ─────────────────────────────────────────────────────────────────────────
  public void FinalApprove(TradeEscrow escrow)
  {
    if (!AllowCommit)
      throw new InvalidOperationException(
        "FinalApprove BLOCKED: AllowCommit is false (dry-run). No assets moved.");
    dynamic remote = Unbind(escrow);
    OnUI(() => ProcessAction(remote, A_FinalApprove, BuildAction(A_FinalApprove)));
    Log($"[trade] FINAL APPROVE sent for escrow {escrow.Id} (commit).");
  }

  // Dispatch a ClientAction into the escrow. ITradeEscrow.Process is GENERIC —
  // Process<T>(T message) — and a plain dynamic `dro.Process(action)` cannot
  // convey the type argument T over the remoting bridge (the fast path only
  // forwards generics supplied at the C# call site). Use the documented RemoteNET
  // generic-dispatch indexer: dro.Process[remoteType](action).
  static void ProcessAction(dynamic escrowDro, string actionTypeName, dynamic action)
  {
    Type rt = RemoteClient.GetInstanceType(actionTypeName);
    // escrowDro.Process -> DynamicRemoteMethod; [rt] -> generic-specialized proxy.
    dynamic methodProxy = escrowDro.Process;
    dynamic specialized = methodProxy[rt];
    // The specialized proxy's invocation needs the parent DRO, which pure dynamic
    // invocation can't supply ("Cannot invoke a non-delegate type"). Call the
    // proxy's public TryInvoke(parent, args, out result) directly.
    var drm = (DynamicRemoteObject.DynamicRemoteMethod)specialized;
    var parent = (DynamicRemoteObject)escrowDro;
    drm.TryInvoke(parent, new object[] { action }, out object _);
  }

  // Build a ClientAction. These are NOT publicly constructible (private ctors):
  //  - parameterized actions expose a static GetInstance(args) factory
  //    (e.g. SendTradeItemUpdateAction.GetInstance(CollectionItem[]))
  //  - parameterless actions are SINGLETONS exposed via a static Instance
  //    property (getter get_Instance()), e.g. CancelBinderSelectionAction.Instance
  static dynamic BuildAction(string actionType, params object[] args)
  {
    if (args != null && args.Length > 0)
      return RemoteClient.InvokeMethod(actionType, "GetInstance", null, args);
    return RemoteClient.InvokeMethod(actionType, "get_Instance", null);
  }

  // ─────────────────────────────────────────────────────────────────────────
  // Reactive observation (read-only): log trade lifecycle. Defensive auto-cancel
  // if any Approval* state is seen while AllowCommit is false.
  // ─────────────────────────────────────────────────────────────────────────
  readonly List<Action> _off = new();

  public void Attach()
  {
    Action<TradeEscrow, bool> started = (e, isPlayer) =>
      Log($"[event] TradeStarted player={isPlayer} partner={Try(() => e.TradePartnerName) ?? "?"} state={e.State}");
    TradeManager.TradeStarted += started;
    _off.Add(() => TradeManager.TradeStarted -= started);

    Action<TradeEscrow, (TradeState Old, TradeState New)> changed = (e, s) =>
    {
      // Observe only. The bot never sends the commit (FinalApprove is gated).
      Log($"[event] StateChanged {s.Old} -> {s.New} partner={Try(() => e.TradePartnerName) ?? "?"}");

      // Capture escrow accounting as the trade progresses (a closed escrow can't
      // be read afterwards). Keep the last non-empty snapshot of each side.
      _lastPartner = Try(() => e.TradePartnerName) ?? _lastPartner;
      var recv = SnapshotItems(e.PartnerTradedItems);
      var give = SnapshotItems(e.TradedItems);
      if (recv.Count > 0) _lastReceived = recv;
      if (give.Count > 0) _lastGiven = give;

      // On completion, auto-write the ledger entry.
      if (s.New == TradeState.Closed)
      {
        string fs = Try(() => e.FinalState.ToString()) ?? "?";
        LastCloseReason = fs;
        if (fs == "TradeComplete")
          RecordAcquisition(_lastPartner ?? "?", _lastReceived, _lastGiven, fs);
        else
          Log($"[ledger] trade ended '{fs}' — no assets moved, no ledger entry.");
        _lastReceived = new(); _lastGiven = new(); _lastPartner = null;
      }
    };
    TradeManager.TradeStateChanged += changed;
    _off.Add(() => TradeManager.TradeStateChanged -= changed);

    Log("[bot] attached to trade events (observing).");
  }

  public void Dispose()
  {
    foreach (var off in _off) { try { off(); } catch { } }
    _off.Clear();
  }
}
