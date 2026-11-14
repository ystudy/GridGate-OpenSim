using System;
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

    Task Delay(TimeSpan t);

    void Warn(string text);

    void Error(string text);
}

/// <summary>
/// Every money action a viewer or script can take in a region (money design §7.3). The checks in
/// <see cref="MoneyRules"/> run first; then the service decides. Every call gets a fresh transaction id; a retried step
/// reuses its own.
/// </summary>
internal sealed class MoneyActions(IMoneyService service, Guid regionId, Func<EconomyInfo> economy, IMoneyView view)
{
    /// <summary>libomv's transaction types, which the viewer's notices are keyed by.</summary>
    public static readonly Type TransactionTypes = typeof(MoneyTransactionType);

    // Complete and return are retried this long apart while the service is unavailable (review focus 1).
    private static readonly TimeSpan[] SettleRetries = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(9)];
    private static readonly TimeSpan EntryRetry = TimeSpan.FromSeconds(2);

    public async Task PayAvatarAsync(Payer p, Guid source, MoneyWho to, int amount)
    {
        if (MoneyRules.CheckPayAvatar(p.AgentId, source, amount) is var refusal && refusal != Refusal.None)
        {
            view.Tell(p.AgentId, MoneyRules.Message(refusal));
            return;
        }
        var answer = await service.TransferAsync(Guid.NewGuid(), "pay_avatar", p.Who, to, amount, regionId, null, null, CancellationToken.None);
        if (!answer.Ok)
        {
            view.Tell(p.AgentId, MoneyRules.Message(answer.Code));
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
        var answer = await service.TransferAsync(Guid.NewGuid(), "pay_object", p.Who, owner, amount, regionId, part.PartId, root.Name,
            CancellationToken.None);
        if (!answer.Ok)
        {
            view.Tell(p.AgentId, MoneyRules.Message(answer.Code));
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
    /// </summary>
    public async Task<bool> BuyAsync(Payer p, PartInfo part, MoneyWho seller, byte saleType, int salePrice, Func<bool> deliver)
    {
        if (MoneyRules.CheckBuy(part, saleType, salePrice) is var refusal && refusal != Refusal.None)
        {
            view.Tell(p.AgentId, MoneyRules.Message(refusal));
            return false;
        }
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
        try
        {
            delivered = deliver();
        }
        catch (Exception e)
        {
            view.Warn($"Delivering {part.PartId} to {p.AgentId} failed with {e.GetType().Name}; returning hold {holdId}.");
            delivered = false;
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

    /// <summary>
    /// Holds the price of a parcel. The hold id, 0 for free land (nothing held), or null after telling the buyer why not.
    /// </summary>
    public async Task<long?> HoldLandAsync(Payer p, MoneyWho seller, int price, int parcelLocalId)
    {
        if (price <= 0) return 0;
        var hold = await service.HoldAsync(Guid.NewGuid(), "land", p.Who, seller, price, regionId, null, $"parcel {parcelLocalId}", CancellationToken.None);
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
    public bool MovePass(Payer p, MoneyWho parcelOwner, int amount)
    {
        if (amount < 0) return false;
        if (amount == 0) return true;
        var answer = Wait(service.TransferAsync(Guid.NewGuid(), "parcel_pass", p.Who, parcelOwner, amount, regionId, null, null, CancellationToken.None));
        if (!answer.Ok)
        {
            view.Tell(p.AgentId, MoneyRules.Message(answer.Code));
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
        if (to.Home.Length > 0 && !economy().VisitorWallets)
        {
            reason = "LINDENDOLLAR_ENTITYDOESNOTEXIST";
            return false;
        }
        var answer = Wait(service.PayoutAsync(txn == Guid.Empty ? Guid.NewGuid() : txn, ownerId, to, amount, regionId, objectId, objectName,
            CancellationToken.None));
        if (!answer.Ok)
        {
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
        var answer = Wait(service.ChargeAsync(Guid.NewGuid(), type, p.Who, amount, payTo, CancellationToken.None));
        if (!answer.Ok)
        {
            view.Warn($"Uncollected {type} of {amount} from {p.AgentId}: {answer.Code}");
            return;
        }
        if (answer.Balance is { } after)
            view.PushBalance(p.AgentId, after, new BalanceNote(ChargeType(type), p.AgentId, payTo ?? Guid.Empty, amount, ""));
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
        for (var attempt = 0; ; attempt++)
        {
            var answer = complete
                ? await service.CompleteAsync(txn, holdId, CancellationToken.None)
                : await service.ReturnAsync(txn, holdId, CancellationToken.None);
            if (answer.Code != MoneyAnswer.UnavailableCode) return answer;
            if (attempt == SettleRetries.Length)
            {
                view.Error($"Couldn't {(complete ? "complete" : "return")} hold {holdId}; the money service returns it to the buyer when it expires.");
                return answer;
            }
            await view.Delay(SettleRetries[attempt]);
        }
    }

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
    /// waits at most <see cref="MoneyServiceClient.Timeout"/>.
    /// </summary>
    private static T Wait<T>(Task<T> task) => task.GetAwaiter().GetResult();
}
