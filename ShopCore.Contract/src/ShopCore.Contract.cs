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
    InternalError = 9
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
    bool CanBeSold = true
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
public interface IShopCoreApiV1
{
    /// <summary>
    /// Wallet kind used by the shop economy.
    /// </summary>
    string WalletKind { get; }

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
}