using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenMetaverse;

namespace GridGate.Region.Money;

/// <summary>
/// The viewer's notice that comes with a balance: what moved, between whom, for what. Its numbers are libomv's
/// <see cref="MoneyTransactionType"/>.
/// </summary>
internal sealed record BalanceNote(int TransactionType, Guid Source, Guid Dest, int Amount, string Item);

/// <summary>
/// The avatar who acts. The module always builds it from the viewer's own client, never from a packet's ids, so its
/// session is the one Robust gave this viewer.
/// </summary>
internal sealed record Payer(Guid AgentId, string Home, Guid SessionId, Guid SecureSessionId)
{
    public MoneyWho Who => new(AgentId, Home, SessionId, SecureSessionId);
}

/// <summary>How the actions reach the viewer, the scene and the log; the module implements it.</summary>
internal interface IMoneyView
{
    void Tell(Guid agent, string text);

    /// <summary>A balance for every viewer the agent has here; a null note is a silent update.</summary>
    void PushBalance(Guid agent, long balance, BalanceNote? note);

    /// <summary>Raises <c>OnObjectPaid</c>, which runs the prim's <c>money()</c> event.</summary>
    void ObjectPaid(Guid partId, Guid payer, int amount);

    Task Delay(TimeSpan t, CancellationToken ct = default);

    void Warn(string text);

    void Error(string text);
}

/// <summary>
/// Every money action a viewer or script can take in a region (money design §7.3). The checks in
/// <see cref="MoneyRules"/> run first; then the service decides. Every payment and every settling of a hold gets a fresh
/// transaction id and is sent again with that same id while the service is unavailable: the service answers a repeat with
/// its first answer, so nothing is paid twice.
/// </summary>
internal sealed class MoneyActions(IMoneyService service, Guid regionId, Func<EconomyInfo?> economy, IMoneyView view)
{
    /// <summary>libomv's transaction types, which the viewer's notices are keyed by.</summary>
    public static readonly Type TransactionTypes = typeof(MoneyTransactionType);

    // Payments, completes and returns are sent again this long apart while the service is unavailable.
    private static readonly TimeSpan[] Retries = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(9)];
    private static readonly TimeSpan EntryRetry = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long a synchronous payment (a parcel pass, a script payout) may take in all, its attempts and the waits between
    /// them together: one service call's timeout, so OpenSim's thread is held no longer than by a single call.
    /// </summary>
    public TimeSpan SyncBudget { get; init; } = MoneyServiceClient.Timeout;

    /// <summary>How long script payouts fail at once after one got no answer from the service.</summary>
    public static readonly TimeSpan PayoutPause = TimeSpan.FromSeconds(30);

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>
    /// When the pause ends, as a <see cref="TimeProvider.GetTimestamp"/> value. Shared by every region of a Region Server
    /// (the module passes one to each), so one dead money service is noticed once, not once per region.
    /// </summary>
    public PayoutPauseState Pause { get; init; } = new();

    internal sealed class PayoutPauseState
    {
        public long Until;
    }

    public async Task PayAvatarAsync(Payer p, Guid source, MoneyWho to, int amount)
    {
        if (MoneyRules.CheckPayAvatar(p.AgentId, source, amount) is var refusal && refusal != Refusal.None)
        {
            view.Tell(p.AgentId, MoneyRules.Message(refusal));
            return;
        }
        var txn = Guid.NewGuid();
        var answer = await RetriedAsync(ct => service.TransferAsync(txn, "pay_avatar", p.Who, to, amount, regionId, null, null, ct));
        if (!answer.Ok)
        {
            Refused(p, answer, "pay_avatar", txn, to.AgentId, amount);
            return;
        }
        PushBoth(p.AgentId, answer.Balance, to.AgentId, answer.ToBalance, new BalanceNote((int)MoneyTransactionType.Gift, p.AgentId, to.AgentId, amount, ""));
    }

    /// <summary>A click on Pay for <paramref name="part"/>; <paramref name="root"/> carries the object's pay prices and owner.</summary>
    public async Task PayObjectAsync(Payer p, Guid source, PartInfo part, PartInfo root, MoneyWho owner, int amount)
    {
        if (MoneyRules.CheckPayObject(p.AgentId, source, amount, root) is var refusal && refusal != Refusal.None)
        {
            view.Tell(p.AgentId, MoneyRules.Message(refusal));
            return;
        }
        var txn = Guid.NewGuid();
        var answer = await RetriedAsync(ct => service.TransferAsync(txn, "pay_object", p.Who, owner, amount, regionId, part.PartId, root.Name, ct));
        if (!answer.Ok)
        {
            Refused(p, answer, "pay_object", txn, owner.AgentId, amount);
            return;
        }
        // The script hears of it only once the coins have moved (money design §7.3).
        view.ObjectPaid(part.PartId, p.AgentId, amount);
        PushBoth(p.AgentId, answer.Balance, owner.AgentId, answer.ToBalance,
            new BalanceNote((int)MoneyTransactionType.PayObject, p.AgentId, owner.AgentId, amount, root.Name));
    }

    /// <summary>
    /// Buying an object: the buyer's coins go on hold, the object is delivered, then the hold completes; a delivery that
    /// fails returns them. Free things, and your own, are delivered without money.
    /// <para>
    /// Buys of one object run one after another, and <paramref name="reread"/> (the object as it is now) is checked again
    /// once a buy's turn comes and again after its hold: OpenSim's delivery doesn't check the sale, so without this two
    /// buyers of an Original could both pay and only one get it (final review, Critical 1).
    /// </para>
    /// </summary>
    public async Task<bool> BuyAsync(Payer p, PartInfo part, MoneyWho seller, byte saleType, int salePrice, Func<bool> deliver,
        Func<PartInfo?>? reread = null)
    {
        if (MoneyRules.CheckBuy(part, saleType, salePrice) is var refusal && refusal != Refusal.None)
        {
            view.Tell(p.AgentId, MoneyRules.Message(refusal));
            return false;
        }
        var turn = BuyLocks[(int)((uint)part.RootId.GetHashCode() % BuyLocks.Length)];
        await turn.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Changed(p, reread, seller, saleType, salePrice)) return false;
            if (salePrice == 0 || seller.AgentId == p.AgentId) return deliver();

            var hold = await service.HoldAsync(Guid.NewGuid(), "object", p.Who, seller, salePrice, regionId, part.PartId, part.Name, CancellationToken.None);
            if (!hold.Ok || hold.HoldId is not { } holdId)
            {
                view.Tell(p.AgentId, MoneyRules.Message(hold.Ok ? MoneyAnswer.UnavailableCode : hold.Code));
                return false;
            }
            var note = new BalanceNote((int)MoneyTransactionType.ObjectSale, p.AgentId, seller.AgentId, salePrice, part.Name);
            if (hold.Balance is { } after) view.PushBalance(p.AgentId, after, note);

            bool delivered;
            if (Changed(p, reread, seller, saleType, salePrice))
            {
                delivered = false;
            }
            else
            {
                try
                {
                    delivered = deliver();
                }
                catch (Exception e)
                {
                    view.Warn($"Delivering {part.PartId} to {p.AgentId} failed with {e.GetType().Name}; returning hold {holdId}.");
                    delivered = false;
                }
            }

            if (delivered)
            {
                var done = await SettleAsync(holdId, complete: true);
                if (done.Ok && done.ToBalance is { } sellerAfter) view.PushBalance(seller.AgentId, sellerAfter, note);
                return true;
            }
            var back = await SettleAsync(holdId, complete: false);
            if (back.Ok && back.ToBalance is { } buyerAfter) view.PushBalance(p.AgentId, buyerAfter, null);
            return false;
        }
        finally
        {
            turn.Release();
        }
    }

    // Buys are serialized per object through one of these, picked by the root's id.
    private static readonly SemaphoreSlim[] BuyLocks = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    /// <summary>True (after telling the buyer) when the object is gone, or its sale or owner is no longer what was clicked.</summary>
    private bool Changed(Payer p, Func<PartInfo?>? reread, MoneyWho seller, byte saleType, int salePrice)
    {
        if (reread is null) return false;
        var now = reread();
        var refusal = now is null ? Refusal.NotForSale : MoneyRules.CheckBuy(now, saleType, salePrice);
        if (refusal == Refusal.None && now!.OwnerId == seller.AgentId) return false;
        view.Tell(p.AgentId, MoneyRules.Message(refusal == Refusal.None ? Refusal.PriceChanged : refusal));
        return true;
    }

    /// <summary>
    /// Holds the price of a parcel. The hold id, 0 for free land (nothing held), or null after telling the buyer why not.
    /// </summary>
    /// <param name="limit">How long to wait for the service: the land module calls on the region's packet thread.</param>
    public async Task<long?> HoldLandAsync(Payer p, MoneyWho seller, int price, int parcelLocalId, TimeSpan? limit = null)
    {
        if (price <= 0) return 0;
        using var cts = new CancellationTokenSource(limit ?? MoneyServiceClient.Timeout);
        MoneyAnswer hold;
        try
        {
            hold = await service.HoldAsync(Guid.NewGuid(), "land", p.Who, seller, price, regionId, null, $"parcel {parcelLocalId}", cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // A hold the service did place after all is returned to the buyer when it expires.
            hold = MoneyAnswer.Unavailable;
        }
        if (!hold.Ok || hold.HoldId is not { } holdId)
        {
            view.Tell(p.AgentId, MoneyRules.Message(hold.Ok ? MoneyAnswer.UnavailableCode : hold.Code));
            return null;
        }
        if (hold.Balance is { } after)
            view.PushBalance(p.AgentId, after, new BalanceNote((int)MoneyTransactionType.LandSale, p.AgentId, seller.AgentId, price, ""));
        return holdId;
    }

    /// <summary>After the land module ran: complete when the buyer is now the owner, otherwise return the coins.</summary>
    public async Task SettleLandAsync(Payer p, MoneyWho seller, long holdId, bool newOwnerConfirmed)
    {
        var answer = await SettleAsync(holdId, newOwnerConfirmed);
        if (!answer.Ok || answer.ToBalance is not { } after) return;
        if (newOwnerConfirmed) view.PushBalance(seller.AgentId, after, null);
        else view.PushBalance(p.AgentId, after, null);
    }

    /// <summary>A parcel pass, for the land module's synchronous <c>MoveMoney</c>; true only when the coins moved.</summary>
    public bool MovePass(Payer p, MoneyWho parcelOwner, int amount, bool groupLand = false)
    {
        if (amount < 0)
        {
            view.Tell(p.AgentId, MoneyRules.Message("unavailable"));
            return false;
        }
        // A group holds no coins on this grid (money design §7.3; money review 2026-11-15, R-9). OpenSim's land module
        // refuses this first today; this doesn't rely on it.
        if (groupLand)
        {
            view.Tell(p.AgentId, MoneyRules.GroupPassText);
            return false;
        }
        if (amount == 0) return true;
        var txn = Guid.NewGuid();
        var answer = Wait(RetriedAsync(ct => service.TransferAsync(txn, "parcel_pass", p.Who, parcelOwner, amount, regionId, null, null, ct), SyncBudget));
        if (!answer.Ok)
        {
            Refused(p, answer, "parcel_pass", txn, parcelOwner.AgentId, amount);
            return false;
        }
        PushBoth(p.AgentId, answer.Balance, parcelOwner.AgentId, answer.ToBalance,
            new BalanceNote((int)MoneyTransactionType.LandPassSale, p.AgentId, parcelOwner.AgentId, amount, ""));
        return true;
    }

    /// <summary>
    /// A script paying out (<c>llGiveMoney</c>, <c>llTransferLindenDollars</c>). <paramref name="reason"/> is the script's
    /// reason when it fails, "" when it worked.
    /// </summary>
    public bool Payout(Guid ownerId, MoneyWho to, int amount, Guid objectId, string objectName, Guid txn, out string reason)
    {
        if (amount <= 0)
        {
            reason = "INVALID_AMOUNT";
            return false;
        }
        if (to.Home.Length > 0 && economy()?.VisitorWallets != true)
        {
            reason = "LINDENDOLLAR_ENTITYDOESNOTEXIST";
            return false;
        }
        if (Interlocked.Read(ref Pause.Until) is var until && until != 0 && Time.GetTimestamp() < until)
        {
            reason = "SERVICE_ERROR";
            return false;
        }
        if (txn == Guid.Empty) txn = Guid.NewGuid();
        var answer = Wait(RetriedAsync(ct => service.PayoutAsync(txn, ownerId, to, amount, regionId, objectId, objectName, ct), SyncBudget));
        if (!answer.Ok)
        {
            if (answer.Lost)
            {
                Unknown("payout", txn, ownerId, to.AgentId, amount);
                var now = Time.GetTimestamp();
                var wasPaused = Interlocked.Exchange(ref Pause.Until, now + (long)(PayoutPause.TotalSeconds * Time.TimestampFrequency)) > now;
                if (!wasPaused) view.Warn($"The money service didn't answer a payout; script payouts fail at once for {PayoutPause.TotalSeconds:0} seconds.");
            }
            reason = MoneyRules.ScriptReason(answer.Code);
            return false;
        }
        PushBoth(ownerId, answer.Balance, to.AgentId, answer.ToBalance,
            new BalanceNote((int)MoneyTransactionType.ObjectPays, ownerId, to.AgentId, amount, objectName));
        reason = "";
        return true;
    }

    /// <summary>Whether a fresh balance covers <paramref name="amount"/>; anything but a clear yes is no.</summary>
    public bool Covered(Payer p, long amount)
    {
        if (amount <= 0) return true;
        var answer = Wait(service.BalanceAsync(p.Who, CancellationToken.None));
        return answer.Ok && answer.Balance >= amount;
    }

    /// <summary>
    /// A fee taken after the action already happened (OpenSim gives no way to undo it). One that can't be collected is
    /// logged and not retried (money design §7.3).
    /// </summary>
    public void Charge(Payer p, string type, int amount, Guid? payTo)
    {
        if (amount <= 0) return;
        var answer = Wait(service.ChargeAsync(Guid.NewGuid(), type, p.Who, amount, payTo, regionId, CancellationToken.None));
        if (!answer.Ok)
        {
            view.Warn($"Uncollected {type} of {amount} from {p.AgentId}: {answer.Code}");
            return;
        }
        if (answer.Balance is { } after)
            view.PushBalance(p.AgentId, after, new BalanceNote(ChargeType(type), p.AgentId, payTo ?? Guid.Empty, amount, ""));
    }

    /// <summary>Whether the service can hold a fee before its action (a service from before that can't).</summary>
    public bool FeeHolds => economy()?.FeeHolds == true;

    /// <summary>
    /// Holds a fee (<c>upload_fee</c>, <c>classified_fee</c>, or <c>group_join_fee</c> paying <paramref name="payTo"/>, the
    /// group's founder) before its action, waiting on the caller's thread as <see cref="Covered"/> does. The hold id; 0 when
    /// nothing is due; null when it can't be paid, after telling the payer why, except for too few coins, which the
    /// caller says in its own words. Before the service's first economy answer nothing is held.
    /// </summary>
    public long? HoldFee(Payer p, string type, int amount, Guid? payTo)
    {
        if (!FeesKnown(p)) return null;
        if (amount <= 0) return 0;
        var founder = payTo is { } f ? new MoneyWho(f, "") : null;
        var hold = Wait(service.HoldAsync(Guid.NewGuid(), type, p.Who, founder, amount, regionId, null, null, CancellationToken.None));
        if (!hold.Ok || hold.HoldId is not { } holdId)
        {
            // A hold the service did place after all, without an answer, is returned to the payer when it expires.
            if (hold.Code != "not_enough") view.Tell(p.AgentId, MoneyRules.Message(hold.Ok ? MoneyAnswer.UnavailableCode : hold.Code));
            return null;
        }
        if (hold.Balance is { } after)
            view.PushBalance(p.AgentId, after, new BalanceNote(ChargeType(type), p.AgentId, payTo ?? Guid.Empty, amount, ""));
        return holdId;
    }

    /// <summary>
    /// After the fee's action: completes the hold when it <paramref name="succeeded"/> (a founder sees their new balance),
    /// otherwise returns the fee (the payer sees theirs). Retried with one transaction id while the service is unavailable.
    /// </summary>
    public async Task SettleFeeAsync(Payer p, string type, int amount, Guid? payTo, long holdId, bool succeeded)
    {
        var answer = await SettleAsync(holdId, succeeded).ConfigureAwait(false);
        // Completed into Fees, the answer has no balance: Fees is nobody's.
        if (!answer.Ok || answer.ToBalance is not { } after) return;
        if (!succeeded) view.PushBalance(p.AgentId, after, null);
        else if (payTo is { } founder) view.PushBalance(founder, after, new BalanceNote(ChargeType(type), p.AgentId, founder, amount, ""));
    }

    /// <summary>
    /// The classified ad fee: always the grid's own setting. OpenSim passes the listing price the viewer typed
    /// (<c>UserProfileModule</c>), which would let a resident pick their own fee (found in the live checks).
    /// </summary>
    public void ChargeClassified(Payer p, int viewerPrice)
    {
        _ = viewerPrice;
        Charge(p, "classified_fee", ClassifiedFee(), null);
    }

    /// <summary>The grid's classified fee, never negative.</summary>
    public int ClassifiedFee() => (int)Math.Clamp(economy()?.ClassifiedFee ?? 0, 0, int.MaxValue);

    /// <summary>Whether <paramref name="p"/> can pay the grid's classified fee: the check before the ad is saved.</summary>
    /// <summary>
    /// Whether the classified fee is covered. Before the service's first economy answer the fee isn't known, so nothing is
    /// offered free (money review 2026-11-15, R-8): the answer is no.
    /// </summary>
    public bool ClassifiedCovered(Payer p) => FeesKnown(p) && Covered(p, ClassifiedFee());

    /// <summary>Whether an upload's fee is covered; no before the fee is known, as for <see cref="ClassifiedCovered"/>.</summary>
    public bool UploadCovered(Payer p, int amount) => FeesKnown(p) && Covered(p, amount);

    private bool FeesKnown(Payer p)
    {
        if (economy() is not null) return true;
        view.Tell(p.AgentId, MoneyRules.NoEconomyText);
        return false;
    }

    /// <summary>
    /// The balance shown on arrival. Robust may not have moved the avatar's Presence here yet, so a wrong_region is
    /// tried once more after 2 s (review focus 2); nothing is shown unless the service answered.
    /// </summary>
    public async Task PushEntryBalanceAsync(Payer p)
    {
        var answer = await service.BalanceAsync(p.Who, CancellationToken.None);
        if (answer.Code == "wrong_region")
        {
            await view.Delay(EntryRetry);
            answer = await service.BalanceAsync(p.Who, CancellationToken.None);
        }
        if (answer.Ok && answer.Balance is { } balance) view.PushBalance(p.AgentId, balance, null);
    }

    /// <summary>Completes or returns a hold, retried with the same transaction id while the service is unavailable.</summary>
    private async Task<MoneyAnswer> SettleAsync(long holdId, bool complete)
    {
        var txn = Guid.NewGuid();
        var answer = await RetriedAsync(ct => complete
            ? service.CompleteAsync(txn, holdId, ct)
            : service.ReturnAsync(txn, holdId, ct));
        var verb = complete ? "complete" : "return";
        if (answer.Code == MoneyAnswer.UnavailableCode)
            view.Error($"Couldn't {verb} hold {holdId}; the money service returns it to the buyer when it expires.");
        // A refusal here leaves the hold to expire: a delivered object stays paid for by nobody, or a buyer waits for coins.
        else if (!answer.Ok)
            view.Error($"Couldn't {verb} hold {holdId}: {answer.Code}.");
        return answer;
    }

    /// <summary>
    /// Calls <paramref name="send"/>, which reuses one transaction id, until the answer isn't "unavailable" or "outcome_unknown", the call was
    /// turned away (<see cref="MoneyAnswer.Final"/>), <see cref="Retries"/> run out, or <paramref name="budget"/> (the
    /// attempts and the waits between them together) is spent; an attempt the budget cuts short got no answer. Any final
    /// answer but Ok is <see cref="MoneyAnswer.Lost"/> when an attempt got no answer: that attempt may have gone through,
    /// whatever a later one was told.
    /// </summary>
    private async Task<MoneyAnswer> RetriedAsync(Func<CancellationToken, Task<MoneyAnswer>> send, TimeSpan? budget = null)
    {
        using var limit = budget is { } b ? new CancellationTokenSource(b) : null;
        var ct = limit?.Token ?? CancellationToken.None;
        var lost = false;
        for (var attempt = 0; ; attempt++)
        {
            MoneyAnswer answer;
            try
            {
                answer = await send(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                answer = MoneyAnswer.NoAnswer;
            }
            if (answer.Ok) return answer;
            if (answer.Code is not (MoneyAnswer.UnavailableCode or MoneyAnswer.OutcomeUnknownCode)) return lost ? answer with { Lost = true } : answer;
            lost |= answer.Lost;
            if (answer.Final || attempt == Retries.Length || ct.IsCancellationRequested) return answer with { Lost = lost };
            try
            {
                await view.Delay(Retries[attempt], ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return answer with { Lost = lost };
            }
        }
    }

    /// <summary>Tells the payer why a payment didn't go through; one whose answer was lost is logged for staff first.</summary>
    private void Refused(Payer p, MoneyAnswer answer, string type, Guid txn, Guid payee, long amount)
    {
        if (!answer.Lost)
        {
            view.Tell(p.AgentId, MoneyRules.Message(answer.Code));
            return;
        }
        Unknown(type, txn, p.AgentId, payee, amount);
        view.Tell(p.AgentId, MoneyRules.CheckingText);
    }

    /// <summary>A payment that may have gone through or not, logged so staff can find it in the ledger and refund it.</summary>
    private void Unknown(string type, Guid txn, Guid payer, Guid payee, long amount) =>
        view.Error($"The money service never answered {type} {txn}: {amount} from {payer} to {payee}. It may have been paid; check the ledger before refunding.");

    private void PushBoth(Guid payer, long? payerBalance, Guid payee, long? payeeBalance, BalanceNote note)
    {
        if (payerBalance is { } a) view.PushBalance(payer, a, note);
        if (payeeBalance is { } b) view.PushBalance(payee, b, note);
    }

    private static int ChargeType(string type) => type switch
    {
        "upload_fee" => (int)MoneyTransactionType.UploadCharge,
        "group_join_fee" => (int)MoneyTransactionType.GroupJoin,
        "classified_fee" => (int)MoneyTransactionType.ClassifiedCharge,
        _ => 0,
    };

    /// <summary>
    /// For OpenSim's synchronous money calls. The client never captures a context, so blocking here can't deadlock; it
    /// waits at most <see cref="MoneyServiceClient.Timeout"/>, or <see cref="SyncBudget"/> for a retried payment.
    /// </summary>
    private static T Wait<T>(Task<T> task) => task.GetAwaiter().GetResult();
}
