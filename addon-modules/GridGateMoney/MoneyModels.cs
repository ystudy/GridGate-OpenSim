using System;

namespace GridGate.Region.Money;

/// <summary>
/// What the module needs to know about a prim, copied out of the scene so the rules can be tested without one.
/// <see cref="PayPrice"/> is <c>SceneObjectPart.PayPrice</c>: index 0 the free-amount box, 1 to 4 the buttons.
/// </summary>
internal sealed record PartInfo(
    Guid PartId,
    Guid RootId,
    bool IsRoot,
    Guid OwnerId,
    Guid GroupId,
    string Name,
    int[] PayPrice,
    byte SaleType,
    int SalePrice,
    bool HasTransferableContents)
{
    /// <summary>Deeded to its group: a group can't hold coins on this grid (money design §1).</summary>
    public bool GroupOwned => GroupId != Guid.Empty && OwnerId == GroupId;
}

/// <summary>Why the module refused something on its own, before asking the service.</summary>
internal enum Refusal { None, ForgedSource, BadAmount, NotPayPrice, GroupOwned, NotForSale, PriceChanged, NotRoot, NothingToSell, UnknownType }

/// <summary>
/// The only decisions the region module makes (money design §7.3): refusals of what a viewer could forge or what the
/// object doesn't allow. Balances, limits and permissions are the service's.
/// </summary>
internal static class MoneyRules
{
    /// <summary>The script's <c>PAY_HIDE</c> and <c>PAY_DEFAULT</c>.</summary>
    public const int PayHide = -1, PayDefault = -2;

    /// <summary>OpenSim's sale types: original, copy, contents.</summary>
    public const byte SaleOriginal = 1, SaleCopy = 2, SaleContents = 3;

    // The amounts a viewer shows on buttons left at PAY_DEFAULT.
    private static readonly int[] DefaultButtons = [1, 5, 10, 20];

    public const string GroupLandText = "Group-owned land can't be sold for coins on this grid.";
    public const string LandTooFastText = "You're buying land too quickly. Please wait a minute and try again.";
    public const string GroupPassText = "Passes to group-owned land can't be bought with coins on this grid.";
    public const string NoEconomyText = "Fees can't be paid yet: this region hasn't reached the grid's money service. Try again in a minute.";
    public const string VisitorsOffText = "Visitors can't pay or be paid on this grid.";
    public const string CheckingText = "This payment is being checked: the money service didn't answer in time. Please look at your balance before you pay again.";

    public static Refusal CheckPayAvatar(Guid clientAgent, Guid source, int amount) =>
        source != clientAgent ? Refusal.ForgedSource
        : amount <= 0 ? Refusal.BadAmount
        : Refusal.None;

    public static Refusal CheckPayObject(Guid clientAgent, Guid source, int amount, PartInfo root) =>
        CheckPayAvatar(clientAgent, source, amount) is var r && r != Refusal.None ? r
        : root.GroupOwned ? Refusal.GroupOwned
        : !PayPriceAllows(root.PayPrice, amount) ? Refusal.NotPayPrice
        : Refusal.None;

    /// <summary>
    /// A buy click: the part must be the root, for sale as the viewer saw it (type and price), and a contents sale must
    /// have something the buyer can be given.
    /// </summary>
    public static Refusal CheckBuy(PartInfo part, byte saleType, int salePrice)
    {
        if (part.GroupOwned) return Refusal.GroupOwned;
        if (!part.IsRoot) return Refusal.NotRoot;
        if (salePrice < 0 || part.SalePrice < 0) return Refusal.BadAmount;
        if (saleType is not (SaleOriginal or SaleCopy or SaleContents))
            return saleType == 0 || part.SaleType == 0 ? Refusal.NotForSale : Refusal.UnknownType;
        if (part.SaleType != saleType) return Refusal.NotForSale;
        if (part.SalePrice != salePrice) return Refusal.PriceChanged;
        if (saleType == SaleContents && !part.HasTransferableContents) return Refusal.NothingToSell;
        return Refusal.None;
    }

    /// <summary>
    /// A script paying out from <paramref name="root"/> for <paramref name="fromId"/>: the script reason when refused, or null.
    /// A group-owned object has no wallet behind it; any source but the object's owner is someone else's money.
    /// </summary>
    public static string? CheckPayout(PartInfo root, Guid fromId) =>
        root.GroupOwned ? "GROUP_OWNED"
        : fromId != root.OwnerId ? "SERVICE_ERROR"
        : null;

    /// <summary>Whether a land purchase involves a group, on either side: a group holds no coins on this grid.</summary>
    public static bool LandGroupRefused(bool parcelGroupOwned, bool buyForGroup) => parcelGroupOwned || buyForGroup;

    /// <summary>
    /// Whether <paramref name="amount"/> is one the object offers. With the free-amount box shown any positive amount is;
    /// with it hidden, only a button's amount.
    /// </summary>
    public static bool PayPriceAllows(int[] payPrice, int amount)
    {
        if (amount <= 0 || payPrice.Length < 5) return false;
        if (payPrice[0] != PayHide) return true;
        for (var i = 1; i <= 4; i++)
        {
            var button = payPrice[i] == PayDefault ? DefaultButtons[i - 1] : payPrice[i];
            if (button > 0 && button == amount) return true;
        }
        return false;
    }

    /// <summary>The reason <c>llTransferLindenDollars</c> reports for a service code.</summary>
    public static string ScriptReason(string code) => code switch
    {
        "not_enough" => "LINDENDOLLAR_INSUFFICIENTFUNDS",
        "no_wallet" => "LINDENDOLLAR_ENTITYDOESNOTEXIST",
        "not_allowed" => "TRANSFERS_DISABLED",
        "over_daily_limit" or "over_key_limit" => "THROTTLED",
        "bad_amount" => "INVALID_AMOUNT",
        "visitors_off" => "LINDENDOLLAR_ENTITYDOESNOTEXIST",
        "unverified_region" => "REGION_NOT_VERIFIED",
        "unverified_avatar" => "AVATAR_NOT_VERIFIED",
        "visitor_grid_not_listed" => "VISITOR_GRID_NOT_LISTED",
        _ => "SERVICE_ERROR",
    };

    /// <summary>What the viewer is told for a refusal of the module's own. English: the region can't know the viewer's language.</summary>
    public static string Message(Refusal r) => r switch
    {
        Refusal.GroupOwned => "Group-owned objects can't take payments on this grid. Ask the owner to set it to a person.",
        Refusal.NotPayPrice => "That amount isn't one this object accepts.",
        Refusal.NotForSale or Refusal.PriceChanged or Refusal.NotRoot => "This object's sale details changed. Please try again.",
        Refusal.NothingToSell => "This object has nothing to sell.",
        Refusal.BadAmount => "That amount can't be paid.",
        _ => "That payment can't be made.",
    };

    /// <summary>What the viewer is told for a service code other than ok.</summary>
    public static string Message(string code) => code switch
    {
        "not_enough" => "You don't have enough coins for that.",
        "frozen" => "Payments are paused on this grid right now.",
        "account_frozen" => "Your coins are frozen. Please contact the grid's staff.",
        "visitors_off" => VisitorsOffText,
        UnpairedMoneyService.Code => "This region isn't connected to the grid's money yet.",
        "unavailable" => "Money is unavailable right now. Please try again later.",
        "no_wallet" => "That person can't receive coins on this grid.",
        "no_session" or "wrong_region" => "Please log in again to use money here.",
        "bad_amount" => "That amount can't be paid.",
        "not_allowed" => "That payment isn't allowed on this grid.",
        // Verification and limits.
        "unverified_region" => "Money is off in this region: it can't verify who is here. You can still visit.",
        "unverified_avatar" => "Money is paused for you until you next log in: you arrived from a region that can't verify where you are.",
        "visitor_grid_not_listed" => "Visitors from your grid can't use money here. You're welcome to look around.",
        "over_daily_limit" => "You've reached today's spending limit on this grid.",
        "over_key_limit" => "This region has reached today's money limit. Try again later.",
        _ => "That payment couldn't be made. Please try again.",
    };
}
