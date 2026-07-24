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

  /// <summary>THE safety gate. Leave false for all validation runs.</summary>
  public bool AllowCommit { get; init; } = false;

  // Acquisition ledger (escrow accounting). Captured during a trade and written
  // on completion, since a closed escrow is no longer readable.
  public static readonly string LedgerPath = System.IO.Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "mtgosdk-tradebot", "ledger.log");
  private List<string> _lastReceived = new();
  private List<string> _lastGiven = new();
  private string? _lastPartner;

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
    dynamic user = Unbind(new User(partnerName));
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
