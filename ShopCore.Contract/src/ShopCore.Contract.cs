using SwiftlyS2.Shared.Players;

namespace ShopCore.Contract;

/// <summary>
/// Defines the behavior model of a shop item
/// </summary>
public enum ShopItemType
{
    Passive = 0,
    Consumable = 1,
    Temporary = 2,
    Permanent = 3
}

/// <summary>
/// Defines which team can use/purchase an item
/// </summary>
public enum ShopItemTeam
{
    Any = 0,
    T = 2,
    CT = 3
}
/// <summary>
/// Result codes for buy/sell operations.
/// </summary>
public enum ShopTransactionStatus
{
    Success = 0,
    ItemNotFound = 1,
    ItemDisabled = 2,
    TeamNotAllowed = 3,
    AlreadyOwned = 4,
    NotOwned = 5,
    NotSellable = 6,
    InsufficientCredits = 7,
    InvalidAmount = 8,
    InternalError = 9,
    BlockedByModule = 10
}

/// <summary>
/// Mutable context for cancelable pre-action hooks.
/// Modules can block by calling <see cref="Block"/> or <see cref="BlockLocalized"/>.
/// </summary>
public abstract class ShopBeforeActionContext
{
    protected ShopBeforeActionContext(IPlayer player, ShopItemDefinition item)
    {
        Player = player;
        Item = item;
    }

    public IPlayer Player { get; }
    public ShopItemDefinition Item { get; }
    public bool IsBlocked { get; private set; }
    public string Message { get; private set; } = string.Empty;
    public string TranslationKey { get; private set; } = string.Empty;
    public object[] TranslationArgs { get; private set; } = [];

    public void Block(string message)
    {
        IsBlocked = true;
        Message = message ?? string.Empty;
        TranslationKey = string.Empty;
        TranslationArgs = [];
    }

    public void BlockLocalized(string translationKey, params object[] args)
    {
        IsBlocked = true;
        TranslationKey = translationKey ?? string.Empty;
        TranslationArgs = args ?? [];

        if (string.IsNullOrWhiteSpace(Message))
        {
            Message = "Action blocked by module.";
        }
    }
}

/// <summary>
/// Cancelable purchase request context.
/// </summary>
public sealed class ShopBeforePurchaseContext : ShopBeforeActionContext
{
    public ShopBeforePurchaseContext(IPlayer player, ShopItemDefinition item) : base(player, item) { }
}

/// <summary>
/// Cancelable sell request context.
/// </summary>
public sealed class ShopBeforeSellContext : ShopBeforeActionContext
{
    public ShopBeforeSellContext(IPlayer player, ShopItemDefinition item) : base(player, item) { }
}

/// <summary>
/// Cancelable toggle request context.
/// </summary>
public sealed class ShopBeforeToggleContext : ShopBeforeActionContext
{
    public ShopBeforeToggleContext(IPlayer player, ShopItemDefinition item, bool targetEnabled) : base(player, item)
    {
        TargetEnabled = targetEnabled;
    }

    /// <summary>
    /// Desired new enabled state.
    /// </summary>
    public bool TargetEnabled { get; }
}
/// <summary>
/// Immutable definition of one shop item.
/// </summary>
/// <param name="Id">Unique item id (case-insensitive).</param>
/// <param name="DisplayName">Human readable item name.</param>
/// <param name="Category">Category bucket, e.g. Healings.</param>
/// <param name="Price">Buy price in credits.</param>
/// <param name="SellPrice">Optional fixed sell price; if null, implementation may use fallback logic.</param>
/// <param name="Duration">Optional active duration; null means no expiration.</param>
/// <param name="Type">Item behavior type.</param>
/// <param name="Team">Team restriction.</param>
/// <param name="Enabled">Global availability flag.</param>
/// <param name="CanBeSold">Whether selling this item is allowed.</param>
/// <param name="AllowPreview">Whether preview action should appear in buy item menu.</param>
/// <param name="IsEquipable">Whether item should be owned/equipped. Set to false for one-time buy items.</param>
public sealed record ShopItemDefinition(
    string Id,
    string DisplayName,
    string Category,
    decimal Price,
    decimal? SellPrice,
    TimeSpan? Duration,
    ShopItemType Type,
    ShopItemTeam Team,
    bool Enabled = true,
    bool CanBeSold = true,
    bool AllowPreview = true,
    bool IsEquipable = true
);
/// <summary>
/// Unified transaction result for buy/sell operations.
/// </summary>
/// <param name="Status">Operation status code.</param>
/// <param name="Message">Human-readable result message.</param>
/// <param name="Item">Item related to the operation.</param>
/// <param name="CreditsAfter">Player credit balance after operation.</param>
/// <param name="CreditsDelta">Credit delta: negative on buy, positive on sell.</param>
/// <param name="ExpiresAtUnixSeconds">Optional expiration timestamp for active timed items.</param>
public sealed record ShopTransactionResult(
    ShopTransactionStatus Status,
    string Message,
    ShopItemDefinition? Item = null,
    decimal CreditsAfter = 0m,
    decimal CreditsDelta = 0m,
    long? ExpiresAtUnixSeconds = null
);

/// <summary>
/// One ledger entry describing a credits or item transaction.
/// </summary>
public sealed record ShopLedgerEntry(
    long TimestampUnixSeconds,
    ulong SteamId,
    int PlayerId,
    string PlayerName,
    string Action,
    decimal Amount,
    decimal BalanceAfter,
    string? ItemId = null,
    string? ItemDisplayName = null
);
public interface IShopCoreApiV1
{
    /// <summary>
    /// Wallet kind used by the shop economy.
    /// </summary>
    string WalletKind { get; }
    /// <summary>
    /// Fired before purchase processing. Handlers can block this operation.
    /// </summary>
    event Action<ShopBeforePurchaseContext>? OnBeforeItemPurchase;

    /// <summary>
    /// Fired before sell processing. Handlers can block this operation.
    /// </summary>
    event Action<ShopBeforeSellContext>? OnBeforeItemSell;

    /// <summary>
    /// Fired before toggle processing. Handlers can block this operation.
    /// </summary>
    event Action<ShopBeforeToggleContext>? OnBeforeItemToggle;

    /// <summary>
    /// Fired when an item is registered.
    /// </summary>
    event Action<ShopItemDefinition>? OnItemRegistered;

    /// <summary>
    /// Fired when a player purchases an item.
    /// </summary>
    event Action<IPlayer, ShopItemDefinition>? OnItemPurchased;

    /// <summary>
    /// Fired when a player sells an item.
    /// Third argument is the credited sell amount.
    /// </summary>
    event Action<IPlayer, ShopItemDefinition, decimal>? OnItemSold;

    /// <summary>
    /// Fired when item state is toggled for a player.
    /// Third argument is enabled state.
    /// </summary>
    event Action<IPlayer, ShopItemDefinition, bool>? OnItemToggled;

    /// <summary>
    /// Fired when a timed item expires for a player.
    /// </summary>
    event Action<IPlayer, ShopItemDefinition>? OnItemExpired;

    /// <summary>
    /// Fired when a player requests a preview for an item from the buy menu.
    /// Modules can subscribe and implement custom preview behavior.
    /// </summary>
    event Action<IPlayer, ShopItemDefinition>? OnItemPreview;

    /// <summary>
    /// Fired when a ledger entry is recorded.
    /// </summary>
    event Action<ShopLedgerEntry>? OnLedgerEntryRecorded;

    /// <summary>
    /// Registers an item definition.
    /// </summary>
    bool RegisterItem(ShopItemDefinition item);

    /// <summary>
    /// Unregisters an item by id.
    /// </summary>
    bool UnregisterItem(string itemId);

    /// <summary>
    /// Tries to get an item by id.
    /// </summary>
    bool TryGetItem(string itemId, out ShopItemDefinition item);

    /// <summary>
    /// Returns all registered items.
    /// </summary>
    IReadOnlyCollection<ShopItemDefinition> GetItems();

    /// <summary>
    /// Returns all items in a category.
    /// </summary>
    IReadOnlyCollection<ShopItemDefinition> GetItemsByCategory(string category);

    /// <summary>
    /// Loads a module config from centralized ShopCore module config storage.
    /// Returns a new instance of <typeparamref name="T"/> when the file/section is missing or invalid.
    /// </summary>
    /// <typeparam name="T">Target config model type.</typeparam>
    /// <param name="modulePluginId">Module plugin id, e.g. <c>Shop_Healthshot</c>.</param>
    /// <param name="fileName">Config file name (stored under <c>configs/plugins/ShopCore/modules</c>).</param>
    /// <param name="sectionName">Optional top-level section name. Empty means root object.</param>
    T LoadModuleConfig<T>(
        string modulePluginId,
        string fileName = "items_config.jsonc",
        string sectionName = "Main") where T : class, new();

    /// <summary>
    /// Saves a module config into centralized ShopCore module config storage.
    /// </summary>
    /// <typeparam name="T">Config model type.</typeparam>
    /// <param name="modulePluginId">Module plugin id, e.g. <c>Shop_Healthshot</c>.</param>
    /// <param name="config">Config payload to save.</param>
    /// <param name="fileName">Config file name (stored under <c>configs/plugins/ShopCore/modules</c>).</param>
    /// <param name="sectionName">Optional top-level section name. Empty means root object.</param>
    /// <param name="overwrite">If true, replaces existing file.</param>
    /// <returns>True when file is written successfully.</returns>
    bool SaveModuleConfig<T>(
        string modulePluginId,
        T config,
        string fileName = "items_config.jsonc",
        string sectionName = "Main",
        bool overwrite = true) where T : class;

    /// <summary>
    /// Gets player credits by player instance.
    /// </summary>
    decimal GetCredits(IPlayer player);

    /// <summary>
    /// Adds credits to player by player instance.
    /// </summary>
    bool AddCredits(IPlayer player, decimal amount);

    /// <summary>
    /// Subtracts credits from player by player instance.
    /// </summary>
    bool SubtractCredits(IPlayer player, decimal amount);

    /// <summary>
    /// Checks whether player has at least the specified credits.
    /// </summary>
    bool HasCredits(IPlayer player, decimal amount);

    /// <summary>
    /// Purchases an item for a player.
    /// </summary>
    ShopTransactionResult PurchaseItem(IPlayer player, string itemId);

    /// <summary>
    /// Requests a preview action for an item for a player.
    /// Returns true when at least one preview handler ran.
    /// </summary>
    bool PreviewItem(IPlayer player, string itemId);

    /// <summary>
    /// Sells an owned item for a player.
    /// </summary>
    ShopTransactionResult SellItem(IPlayer player, string itemId);

    /// <summary>
    /// Returns whether an item is currently enabled for a player.
    /// </summary>
    bool IsItemEnabled(IPlayer player, string itemId);

    /// <summary>
    /// Sets enabled state for an item for a player.
    /// </summary>
    bool SetItemEnabled(IPlayer player, string itemId, bool enabled);

    /// <summary>
    /// Gets expiration unix timestamp for a player's item; null if none.
    /// </summary>
    long? GetItemExpireAt(IPlayer player, string itemId);

    /// <summary>
    /// Gets recent ledger entries in descending order (newest first).
    /// </summary>
    IReadOnlyCollection<ShopLedgerEntry> GetRecentLedgerEntries(int maxEntries = 100);

    /// <summary>
    /// Gets recent ledger entries for one player in descending order (newest first).
    /// </summary>
    IReadOnlyCollection<ShopLedgerEntry> GetRecentLedgerEntriesForPlayer(IPlayer player, int maxEntries = 50);
}
