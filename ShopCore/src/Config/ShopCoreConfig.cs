namespace ShopCore;

public sealed class ShopCoreConfig
{
    public CommandsConfig Commands { get; set; } = new();
    public CreditsConfig Credits { get; set; } = new();
    public MenusConfig Menus { get; set; } = new();
    public BehaviorConfig Behavior { get; set; } = new();
}

public sealed class CommandsConfig
{
    public bool RegisterAsRawCommands { get; set; } = true;

    public List<string> OpenShopMenu { get; set; } = ["shop", "store"];
    public List<string> OpenBuyMenu { get; set; } = ["buy"];
    public List<string> OpenInventoryMenu { get; set; } = ["inventory", "inv"];
    public List<string> ShowCredits { get; set; } = ["credits", "balance"];
    public List<string> GiftCredits { get; set; } = ["giftcredits", "gift"];

    public AdminCommandsConfig Admin { get; set; } = new();
}

public sealed class AdminCommandsConfig
{
    public string Permission { get; set; } = "shopcore.admin.credits";
    public List<string> GiveCredits { get; set; } = ["givecredits", "addcredits"];
    public List<string> RemoveCredits { get; set; } = ["removecredits", "takecredits", "subcredits"];
}

public sealed class CreditsConfig
{
    public string WalletName { get; set; } = ShopCoreApiV1.DefaultWalletKind;
    public int StartingBalance { get; set; } = 0;
    public bool GrantStartingBalanceOncePerPlayer { get; set; } = true;
    public bool NotifyWhenStartingBalanceApplied { get; set; } = true;

    public TimedIncomeConfig TimedIncome { get; set; } = new();
    public CreditTransferConfig Transfer { get; set; } = new();
    public AdminCreditAdjustmentsConfig AdminAdjustments { get; set; } = new();
}

public sealed class TimedIncomeConfig
{
    public bool Enabled { get; set; } = false;
    public int AmountPerInterval { get; set; } = 0;
    public float IntervalSeconds { get; set; } = 300f;
    public bool NotifyPlayers { get; set; } = false;
}

public sealed class MenusConfig
{
    public bool FreezePlayerWhileOpen { get; set; } = false;
    public bool EnableMenuSound { get; set; } = true;
    public int MaxVisibleItems { get; set; } = 5;
    public string DefaultCommentTranslationKey { get; set; } = "shop.menu.comment";
}

public sealed class BehaviorConfig
{
    public bool AllowSelling { get; set; } = true;
    public decimal DefaultSellRefundRatio { get; set; } = 0.50m;
}

public sealed class CreditTransferConfig
{
    public bool Enabled { get; set; } = true;
    public int MinimumAmount { get; set; } = 1;
    public bool AllowSelfTransfer { get; set; } = false;
    public bool NotifyReceiver { get; set; } = true;
}

public sealed class AdminCreditAdjustmentsConfig
{
    public bool NotifyTargetPlayer { get; set; } = true;
    public bool ClampRemovalToAvailableBalance { get; set; } = true;
}
