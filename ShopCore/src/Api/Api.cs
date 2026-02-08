using Cookies.Contract;
using Economy.Contract;
using ShopCore.Contract;
using SwiftlyS2.Shared.Players;

namespace ShopCore;

internal sealed class ShopCoreApiV1 : IShopCoreApiV1
{
    public const string DefaultWalletKind = "credits";
    private const string CookiePrefix = "shopcore:item";

    private readonly ShopCore plugin;
    private readonly object sync = new();
    private readonly Dictionary<string, ShopItemDefinition> itemsById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> categoryToIds = new(StringComparer.OrdinalIgnoreCase);

    public ShopCoreApiV1(ShopCore plugin)
    {
        this.plugin = plugin;
    }

    public string WalletKind => plugin.Settings.Credits.WalletName;

    public event Action<ShopItemDefinition>? OnItemRegistered;
    public event Action<IPlayer, ShopItemDefinition>? OnItemPurchased;
    public event Action<IPlayer, ShopItemDefinition, decimal>? OnItemSold;
    public event Action<IPlayer, ShopItemDefinition, bool>? OnItemToggled;
    public event Action<IPlayer, ShopItemDefinition>? OnItemExpired;

    public bool RegisterItem(ShopItemDefinition item)
    {
        if (item is null) return false;
        if (string.IsNullOrWhiteSpace(item.Id)) return false;
        if (string.IsNullOrWhiteSpace(item.Category)) return false;
        if (item.Price < 0m) return false;
        if (item.SellPrice.HasValue && item.SellPrice.Value < 0m) return false;
        if (item.Duration.HasValue && item.Duration.Value <= TimeSpan.Zero) return false;

        var normalized = item with
        {
            Id = NormalizeItemId(item.Id),
            Category = item.Category.Trim()
        };

        lock (sync)
        {
            if (itemsById.ContainsKey(normalized.Id))
            {
                return false;
            }

            itemsById[normalized.Id] = normalized;

            if (!categoryToIds.TryGetValue(normalized.Category, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                categoryToIds[normalized.Category] = set;
            }

            set.Add(normalized.Id);
        }

        OnItemRegistered?.Invoke(normalized);
        return true;
    }

    public bool UnregisterItem(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId)) return false;
        var id = NormalizeItemId(itemId);

        lock (sync)
        {
            if (!itemsById.Remove(id, out var removed))
            {
                return false;
            }

            if (categoryToIds.TryGetValue(removed.Category, out var set))
            {
                set.Remove(id);
                if (set.Count == 0)
                {
                    categoryToIds.Remove(removed.Category);
                }
            }
        }

        return true;
    }

    public bool TryGetItem(string itemId, out ShopItemDefinition item)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            item = default!;
            return false;
        }

        lock (sync)
        {
            return itemsById.TryGetValue(NormalizeItemId(itemId), out item!);
        }
    }

    public IReadOnlyCollection<ShopItemDefinition> GetItems()
    {
        lock (sync)
        {
            return itemsById.Values.ToArray();
        }
    }

    public IReadOnlyCollection<ShopItemDefinition> GetItemsByCategory(string category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return Array.Empty<ShopItemDefinition>();
        }

        lock (sync)
        {
            if (!categoryToIds.TryGetValue(category.Trim(), out var ids))
            {
                return Array.Empty<ShopItemDefinition>();
            }

            var result = new List<ShopItemDefinition>(ids.Count);
            foreach (var id in ids)
            {
                if (itemsById.TryGetValue(id, out var item))
                {
                    result.Add(item);
                }
            }

            return result;
        }
    }

    public decimal GetCredits(IPlayer player)
    {
        EnsureApis();
        return plugin.economyApi.GetPlayerBalance(player, WalletKind);
    }

    public bool AddCredits(IPlayer player, decimal amount)
    {
        EnsureApis();
        if (!TryToEconomyAmount(amount, out var creditsAmount))
        {
            return false;
        }

        plugin.economyApi.AddPlayerBalance(player, WalletKind, creditsAmount);
        return true;
    }

    public bool SubtractCredits(IPlayer player, decimal amount)
    {
        EnsureApis();
        if (!TryToEconomyAmount(amount, out var creditsAmount))
        {
            return false;
        }

        if (!plugin.economyApi.HasSufficientFunds(player, WalletKind, creditsAmount))
        {
            return false;
        }

        plugin.economyApi.SubtractPlayerBalance(player, WalletKind, creditsAmount);
        return true;
    }

    public bool HasCredits(IPlayer player, decimal amount)
    {
        EnsureApis();
        if (!TryToEconomyAmount(amount, out var creditsAmount))
        {
            return false;
        }

        return plugin.economyApi.HasSufficientFunds(player, WalletKind, creditsAmount);
    }

    public ShopTransactionResult PurchaseItem(IPlayer player, string itemId)
    {
        EnsureApis();

        if (!TryGetItem(itemId, out var item))
        {
            return Fail(
                ShopTransactionStatus.ItemNotFound,
                "Item not found.",
                player,
                "shop.error.item_not_found",
                itemId
            );
        }

        if (!item.Enabled)
        {
            return Fail(
                ShopTransactionStatus.ItemDisabled,
                "Item is disabled.",
                player,
                "shop.error.item_disabled",
                item.DisplayName
            );
        }

        if (!IsTeamAllowed(player, item.Team))
        {
            return Fail(
                ShopTransactionStatus.TeamNotAllowed,
                "Team is not allowed.",
                player,
                "shop.error.team_not_allowed",
                item.DisplayName
            );
        }

        if (IsItemEnabled(player, item.Id))
        {
            return Fail(
                ShopTransactionStatus.AlreadyOwned,
                "Item already enabled.",
                player,
                "shop.error.already_owned",
                item.DisplayName
            );
        }

        if (!TryToEconomyAmount(item.Price, out var buyAmount))
        {
            return Fail(
                ShopTransactionStatus.InvalidAmount,
                "Invalid item price for configured economy.",
                player,
                "shop.error.invalid_amount",
                item.DisplayName
            );
        }

        if (!plugin.economyApi.HasSufficientFunds(player, WalletKind, buyAmount))
        {
            return Fail(
                ShopTransactionStatus.InsufficientCredits,
                "Not enough credits.",
                player,
                "shop.error.insufficient_credits",
                item.DisplayName,
                buyAmount
            );
        }

        plugin.economyApi.SubtractPlayerBalance(player, WalletKind, buyAmount);
        plugin.playerCookies.Set(player, EnabledKey(item.Id), true);

        long? expiresAt = null;
        if (item.Duration.HasValue)
        {
            expiresAt = DateTimeOffset.UtcNow.Add(item.Duration.Value).ToUnixTimeSeconds();
            plugin.playerCookies.Set(player, ExpireAtKey(item.Id), expiresAt.Value);
        }
        else
        {
            plugin.playerCookies.Unset(player, ExpireAtKey(item.Id));
        }

        plugin.playerCookies.Save(player);

        OnItemToggled?.Invoke(player, item, true);
        OnItemPurchased?.Invoke(player, item);

        var creditsAfter = GetCredits(player);
        plugin.SendLocalizedChat(player, "shop.purchase.success", item.DisplayName, buyAmount, creditsAfter);

        return new ShopTransactionResult(
            Status: ShopTransactionStatus.Success,
            Message: "Purchase successful.",
            Item: item,
            CreditsAfter: creditsAfter,
            CreditsDelta: -buyAmount,
            ExpiresAtUnixSeconds: expiresAt
        );
    }

    public ShopTransactionResult SellItem(IPlayer player, string itemId)
    {
        EnsureApis();

        if (!TryGetItem(itemId, out var item))
        {
            return Fail(
                ShopTransactionStatus.ItemNotFound,
                "Item not found.",
                player,
                "shop.error.item_not_found",
                itemId
            );
        }

        if (!plugin.Settings.Behavior.AllowSelling)
        {
            return Fail(
                ShopTransactionStatus.NotSellable,
                "Selling is disabled.",
                player,
                "shop.error.selling_disabled"
            );
        }

        if (!item.CanBeSold)
        {
            return Fail(
                ShopTransactionStatus.NotSellable,
                "Item cannot be sold.",
                player,
                "shop.error.not_sellable",
                item.DisplayName
            );
        }

        if (!IsItemEnabled(player, item.Id))
        {
            return Fail(
                ShopTransactionStatus.NotOwned,
                "Item is not enabled/owned.",
                player,
                "shop.error.not_owned",
                item.DisplayName
            );
        }

        var sellPrice = ResolveSellPrice(item);
        if (!TryToEconomyAmount(sellPrice, out var sellAmount))
        {
            return Fail(
                ShopTransactionStatus.InvalidAmount,
                "Invalid sell amount for configured economy.",
                player,
                "shop.error.invalid_amount",
                item.DisplayName
            );
        }

        plugin.playerCookies.Set(player, EnabledKey(item.Id), false);
        plugin.playerCookies.Unset(player, ExpireAtKey(item.Id));
        plugin.playerCookies.Save(player);

        plugin.economyApi.AddPlayerBalance(player, WalletKind, sellAmount);

        OnItemToggled?.Invoke(player, item, false);
        OnItemSold?.Invoke(player, item, sellAmount);

        var creditsAfter = GetCredits(player);
        plugin.SendLocalizedChat(player, "shop.sell.success", item.DisplayName, sellAmount, creditsAfter);

        return new ShopTransactionResult(
            Status: ShopTransactionStatus.Success,
            Message: "Sell successful.",
            Item: item,
            CreditsAfter: creditsAfter,
            CreditsDelta: sellAmount
        );
    }

    public bool IsItemEnabled(IPlayer player, string itemId)
    {
        EnsureApis();

        if (!TryGetItem(itemId, out var item))
        {
            return false;
        }

        var enabled = plugin.playerCookies.GetOrDefault(player, EnabledKey(item.Id), false);
        if (!enabled)
        {
            return false;
        }

        var expireAt = GetItemExpireAt(player, item.Id);
        if (expireAt.HasValue && expireAt.Value <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            plugin.playerCookies.Set(player, EnabledKey(item.Id), false);
            plugin.playerCookies.Unset(player, ExpireAtKey(item.Id));
            plugin.playerCookies.Save(player);

            OnItemToggled?.Invoke(player, item, false);
            OnItemExpired?.Invoke(player, item);
            plugin.SendLocalizedChat(player, "shop.item.expired", item.DisplayName);

            return false;
        }

        return true;
    }

    public bool SetItemEnabled(IPlayer player, string itemId, bool enabled)
    {
        EnsureApis();

        if (!TryGetItem(itemId, out var item))
        {
            return false;
        }

        plugin.playerCookies.Set(player, EnabledKey(item.Id), enabled);

        if (!enabled)
        {
            plugin.playerCookies.Unset(player, ExpireAtKey(item.Id));
        }
        else if (item.Duration.HasValue)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var current = plugin.playerCookies.GetOrDefault(player, ExpireAtKey(item.Id), 0L);
            if (current <= now)
            {
                var newExpire = DateTimeOffset.UtcNow.Add(item.Duration.Value).ToUnixTimeSeconds();
                plugin.playerCookies.Set(player, ExpireAtKey(item.Id), newExpire);
            }
        }

        plugin.playerCookies.Save(player);
        OnItemToggled?.Invoke(player, item, enabled);
        return true;
    }

    public long? GetItemExpireAt(IPlayer player, string itemId)
    {
        EnsureApis();

        if (!TryGetItem(itemId, out var item))
        {
            return null;
        }

        var value = plugin.playerCookies.GetOrDefault(player, ExpireAtKey(item.Id), 0L);
        return value > 0L ? value : null;
    }

    private static string NormalizeItemId(string itemId) => itemId.Trim().ToLowerInvariant();
    private static string EnabledKey(string itemId) => $"{CookiePrefix}:enabled:{NormalizeItemId(itemId)}";
    private static string ExpireAtKey(string itemId) => $"{CookiePrefix}:expireat:{NormalizeItemId(itemId)}";

    private void EnsureApis()
    {
        if (plugin.playerCookies is null)
        {
            throw new InvalidOperationException("Cookies.Player.V1 is not injected.");
        }

        if (plugin.economyApi is null)
        {
            throw new InvalidOperationException("Economy.API.v1 is not injected.");
        }
    }

    private ShopTransactionResult Fail(
        ShopTransactionStatus status,
        string message,
        IPlayer? player = null,
        string? translationKey = null,
        params object[] args)
    {
        if (player is not null && !string.IsNullOrWhiteSpace(translationKey))
        {
            plugin.SendLocalizedChat(player, translationKey, args);
        }

        return new ShopTransactionResult(
            Status: status,
            Message: message
        );
    }

    private decimal ResolveSellPrice(ShopItemDefinition item)
    {
        if (item.SellPrice.HasValue)
        {
            return item.SellPrice.Value;
        }

        return Math.Round(item.Price * GetSellRefundRatio(), 0, MidpointRounding.AwayFromZero);
    }

    private decimal GetSellRefundRatio()
    {
        var ratio = plugin.Settings.Behavior.DefaultSellRefundRatio;
        if (ratio < 0m)
        {
            return 0m;
        }

        if (ratio > 1m)
        {
            return 1m;
        }

        return ratio;
    }

    private static bool TryToEconomyAmount(decimal amount, out int economyAmount)
    {
        economyAmount = 0;
        if (amount <= 0m)
        {
            return false;
        }

        if (amount != decimal.Truncate(amount))
        {
            return false;
        }

        if (amount > int.MaxValue)
        {
            return false;
        }

        economyAmount = (int)amount;
        return true;
    }

    private static bool IsTeamAllowed(IPlayer player, ShopItemTeam required)
    {
        if (required == ShopItemTeam.Any)
        {
            return true;
        }

        var resolved = ResolvePlayerTeam(player);
        return resolved == required;
    }

    private static ShopItemTeam ResolvePlayerTeam(IPlayer player)
    {
        try
        {
            var controller = player.Controller;
            var t = controller.GetType();

            var raw = t.GetProperty("TeamNum")?.GetValue(controller)
                   ?? t.GetProperty("Team")?.GetValue(controller)
                   ?? t.GetProperty("TeamID")?.GetValue(controller);

            return raw switch
            {
                Team swiftlyTeam => swiftlyTeam switch
                {
                    Team.T => ShopItemTeam.T,
                    Team.CT => ShopItemTeam.CT,
                    _ => ShopItemTeam.Any
                },
                int i when i == 2 => ShopItemTeam.T,
                int i when i == 3 => ShopItemTeam.CT,
                byte b when b == 2 => ShopItemTeam.T,
                byte b when b == 3 => ShopItemTeam.CT,
                _ => ShopItemTeam.Any
            };
        }
        catch
        {
            return ShopItemTeam.Any;
        }
    }
}
