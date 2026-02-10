using System.Text.Json;
using Cookies.Contract;
using Economy.Contract;
using ShopCore.Contract;
using SwiftlyS2.Shared.Players;

namespace ShopCore;

internal sealed class ShopCoreApiV1 : IShopCoreApiV1
{
    public const string DefaultWalletKind = "credits";
    private const string CookiePrefix = "shopcore:item";
    private static readonly JsonSerializerOptions ConfigJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly JsonSerializerOptions ConfigWriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null
    };

    private readonly ShopCore plugin;
    private readonly object sync = new();
    private readonly object ledgerStoreSync = new();
    private readonly object knownModulesSync = new();
    private readonly Dictionary<string, ShopItemDefinition> itemsById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> categoryToIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> knownModulePluginIds = new(StringComparer.OrdinalIgnoreCase);
    private IShopLedgerStore ledgerStore = new InMemoryShopLedgerStore(2000);

    public ShopCoreApiV1(ShopCore plugin)
    {
        this.plugin = plugin;
    }

    public string WalletKind => plugin.Settings.Credits.WalletName;

    public event Action<ShopBeforePurchaseContext>? OnBeforeItemPurchase;
    public event Action<ShopBeforeSellContext>? OnBeforeItemSell;
    public event Action<ShopBeforeToggleContext>? OnBeforeItemToggle;
    public event Action<ShopItemDefinition>? OnItemRegistered;
    public event Action<IPlayer, ShopItemDefinition>? OnItemPurchased;
    public event Action<IPlayer, ShopItemDefinition, decimal>? OnItemSold;
    public event Action<IPlayer, ShopItemDefinition, bool>? OnItemToggled;
    public event Action<IPlayer, ShopItemDefinition>? OnItemExpired;
    public event Action<ShopLedgerEntry>? OnLedgerEntryRecorded;

    internal void ConfigureLedgerStore(LedgerConfig config, string pluginDataDirectory)
    {
        var replacement = CreateLedgerStore(config, pluginDataDirectory);
        IShopLedgerStore previous;
        lock (ledgerStoreSync)
        {
            previous = ledgerStore;
            ledgerStore = replacement;
        }
        previous.Dispose();
    }

    internal void DisposeLedgerStore()
    {
        IShopLedgerStore current;
        lock (ledgerStoreSync)
        {
            current = ledgerStore;
            ledgerStore = new InMemoryShopLedgerStore(100);
        }

        current.Dispose();
    }

    internal string GetLedgerStoreMode()
    {
        lock (ledgerStoreSync)
        {
            return ledgerStore.Mode;
        }
    }

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

    public T LoadModuleTemplateConfig<T>(
        string modulePluginId,
        string fileName = "items_config.jsonc",
        string sectionName = "Main") where T : class, new()
    {
        if (string.IsNullOrWhiteSpace(modulePluginId))
        {
            return new T();
        }

        var effectiveFileName = string.IsNullOrWhiteSpace(fileName) ? "items_config.jsonc" : fileName.Trim();
        var trimmedModulePluginId = modulePluginId.Trim();

        lock (knownModulesSync)
        {
            knownModulePluginIds.Add(trimmedModulePluginId);
        }

        try
        {
            var normalizedFileName = NormalizeRelativeTemplatePath(effectiveFileName);
            if (normalizedFileName is null)
            {
                plugin.LogWarning(
                    "Rejected module template config load due to invalid relative template path '{FileName}'. Module='{ModulePluginId}'.",
                    effectiveFileName,
                    modulePluginId
                );
                return new T();
            }

            var modulePath = plugin.GetPluginPath(modulePluginId);
            if (string.IsNullOrWhiteSpace(modulePath))
            {
                plugin.LogWarning(
                    "Unable to load module template config because plugin path was not found for module '{ModulePluginId}'.",
                    modulePluginId
                );
                return new T();
            }

            var shopCorePath = plugin.GetPluginPath("ShopCore");
            if (string.IsNullOrWhiteSpace(shopCorePath))
            {
                plugin.LogWarning("Unable to resolve ShopCore plugin path while loading module config for '{ModulePluginId}'.", modulePluginId);
                return new T();
            }

            var moduleTemplatePath = Path.Combine(modulePath, "resources", "templates", normalizedFileName);
            var centralizedTemplatePath = Path.Combine(
                shopCorePath,
                "resources",
                "templates",
                "modules",
                modulePluginId,
                normalizedFileName
            );

            EnsureCentralizedTemplate(modulePluginId, moduleTemplatePath, centralizedTemplatePath);

            if (!File.Exists(centralizedTemplatePath))
            {
                CreateFallbackCentralizedTemplate<T>(modulePluginId, centralizedTemplatePath, sectionName);
            }

            if (!File.Exists(centralizedTemplatePath))
            {
                plugin.LogDebug(
                    "Centralized module template config not found for module '{ModulePluginId}'. Expected path: {TemplatePath}",
                    modulePluginId,
                    centralizedTemplatePath
                );
                return new T();
            }

            var rawText = File.ReadAllText(centralizedTemplatePath);
            using var document = JsonDocument.Parse(rawText, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            var payload = document.RootElement;
            if (!string.IsNullOrWhiteSpace(sectionName) &&
                payload.ValueKind == JsonValueKind.Object &&
                payload.TryGetProperty(sectionName, out var sectionElement))
            {
                payload = sectionElement;
            }

            var config = JsonSerializer.Deserialize<T>(payload.GetRawText(), ConfigJsonOptions);
            return config ?? new T();
        }
        catch (Exception ex)
        {
            plugin.LogWarning(
                ex,
                "Failed loading module template config. Module='{ModulePluginId}', File='{FileName}', Section='{SectionName}'.",
                modulePluginId,
                effectiveFileName,
                sectionName
            );
            return new T();
        }
    }

    public bool SaveModuleTemplateConfig<T>(
        string modulePluginId,
        T config,
        string fileName = "items_config.jsonc",
        string sectionName = "Main",
        bool overwrite = true) where T : class
    {
        if (string.IsNullOrWhiteSpace(modulePluginId) || config is null)
        {
            return false;
        }

        var effectiveFileName = string.IsNullOrWhiteSpace(fileName) ? "items_config.jsonc" : fileName.Trim();
        try
        {
            var normalizedFileName = NormalizeRelativeTemplatePath(effectiveFileName);
            if (normalizedFileName is null)
            {
                plugin.LogWarning(
                    "Rejected module template config save due to invalid relative template path '{FileName}'. Module='{ModulePluginId}'.",
                    effectiveFileName,
                    modulePluginId
                );
                return false;
            }

            var shopCorePath = plugin.GetPluginPath("ShopCore");
            if (string.IsNullOrWhiteSpace(shopCorePath))
            {
                plugin.LogWarning("Unable to resolve ShopCore plugin path while saving module config for '{ModulePluginId}'.", modulePluginId);
                return false;
            }

            var centralizedTemplatePath = Path.Combine(
                shopCorePath,
                "resources",
                "templates",
                "modules",
                modulePluginId.Trim(),
                normalizedFileName
            );

            if (!overwrite && File.Exists(centralizedTemplatePath))
            {
                return false;
            }

            var directory = Path.GetDirectoryName(centralizedTemplatePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            object payload = config;
            if (!string.IsNullOrWhiteSpace(sectionName))
            {
                payload = new Dictionary<string, object?>
                {
                    [sectionName] = config
                };
            }

            var serialized = JsonSerializer.Serialize(payload, ConfigWriteOptions);
            File.WriteAllText(centralizedTemplatePath, serialized);

            plugin.LogDebug(
                "Saved centralized module config for '{ModulePluginId}' at '{TemplatePath}'.",
                modulePluginId,
                centralizedTemplatePath
            );

            return true;
        }
        catch (Exception ex)
        {
            plugin.LogWarning(
                ex,
                "Failed saving module template config. Module='{ModulePluginId}', File='{FileName}', Section='{SectionName}'.",
                modulePluginId,
                effectiveFileName,
                sectionName
            );
            return false;
        }
    }

    internal IReadOnlyCollection<string> GetKnownModulePluginIds()
    {
        lock (knownModulesSync)
        {
            return knownModulePluginIds.ToArray();
        }
    }

    private void EnsureCentralizedTemplate(string modulePluginId, string moduleTemplatePath, string centralizedTemplatePath)
    {
        if (File.Exists(centralizedTemplatePath))
        {
            return;
        }

        if (!File.Exists(moduleTemplatePath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(centralizedTemplatePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.Copy(moduleTemplatePath, centralizedTemplatePath, overwrite: false);
        plugin.LogDebug(
            "Created centralized module config template for '{ModulePluginId}' at '{TemplatePath}'.",
            modulePluginId,
            centralizedTemplatePath
        );
    }

    private void CreateFallbackCentralizedTemplate<T>(string modulePluginId, string centralizedTemplatePath, string sectionName) where T : class, new()
    {
        var directory = Path.GetDirectoryName(centralizedTemplatePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        object payload = new T();
        if (!string.IsNullOrWhiteSpace(sectionName))
        {
            payload = new Dictionary<string, object?>
            {
                [sectionName] = payload
            };
        }

        var serialized = JsonSerializer.Serialize(payload, ConfigWriteOptions);
        File.WriteAllText(centralizedTemplatePath, serialized);

        plugin.LogDebug(
            "Created fallback centralized module config for '{ModulePluginId}' at '{TemplatePath}'.",
            modulePluginId,
            centralizedTemplatePath
        );
    }

    private static string? NormalizeRelativeTemplatePath(string fileName)
    {
        var normalized = fileName
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar)
            .Trim();

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        normalized = normalized.TrimStart(Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized))
        {
            return null;
        }

        var segments = normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(static segment => segment == ".."))
        {
            return null;
        }

        return string.Join(Path.DirectorySeparatorChar, segments);
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
        var balanceAfter = plugin.economyApi.GetPlayerBalance(player, WalletKind);
        RecordLedgerEntry(player, "credits_add", creditsAmount, balanceAfter);
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
        var balanceAfter = plugin.economyApi.GetPlayerBalance(player, WalletKind);
        RecordLedgerEntry(player, "credits_subtract", creditsAmount, balanceAfter);
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

        if (TryRunBeforePurchaseHook(player, item, out var blockedByModule))
        {
            return blockedByModule;
        }

        var isConsumable = item.Type == ShopItemType.Consumable;
        if (!isConsumable && IsItemOwned(player, item.Id))
        {
            return Fail(
                ShopTransactionStatus.AlreadyOwned,
                "Item already owned.",
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

        long? expiresAt = null;
        if (!isConsumable)
        {
            plugin.playerCookies.Set(player, OwnedKey(item.Id), true);
            plugin.playerCookies.Set(player, EnabledKey(item.Id), true);

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
        }

        OnItemPurchased?.Invoke(player, item);

        var creditsAfter = GetCredits(player);
        RecordLedgerEntry(player, "purchase", buyAmount, creditsAfter, item);
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

        if (TryRunBeforeSellHook(player, item, out var blockedByModule))
        {
            return blockedByModule;
        }

        if (!IsItemOwned(player, item.Id))
        {
            return Fail(
                ShopTransactionStatus.NotOwned,
                "Item is not owned.",
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

        var wasEnabled = plugin.playerCookies.GetOrDefault(player, EnabledKey(item.Id), false);
        plugin.playerCookies.Set(player, OwnedKey(item.Id), false);
        plugin.playerCookies.Set(player, EnabledKey(item.Id), false);
        plugin.playerCookies.Unset(player, ExpireAtKey(item.Id));
        plugin.playerCookies.Save(player);

        plugin.economyApi.AddPlayerBalance(player, WalletKind, sellAmount);

        if (wasEnabled)
        {
            OnItemToggled?.Invoke(player, item, false);
        }
        OnItemSold?.Invoke(player, item, sellAmount);

        var creditsAfter = GetCredits(player);
        RecordLedgerEntry(player, "sell", sellAmount, creditsAfter, item);
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

        if (!IsItemOwnedInternal(player, item, notifyExpiration: true))
        {
            return false;
        }

        var enabled = plugin.playerCookies.GetOrDefault(player, EnabledKey(item.Id), false);
        return enabled;
    }

    public bool IsItemOwned(IPlayer player, string itemId)
    {
        EnsureApis();

        if (!TryGetItem(itemId, out var item))
        {
            return false;
        }

        return IsItemOwnedInternal(player, item, notifyExpiration: true);
    }

    private bool IsItemOwnedInternal(IPlayer player, ShopItemDefinition item, bool notifyExpiration)
    {
        var owned = plugin.playerCookies.GetOrDefault(player, OwnedKey(item.Id), false);
        var enabled = plugin.playerCookies.GetOrDefault(player, EnabledKey(item.Id), false);

        // Migration path: legacy data stored only "enabled".
        if (!owned && enabled)
        {
            owned = true;
            plugin.playerCookies.Set(player, OwnedKey(item.Id), true);
            plugin.playerCookies.Save(player);
        }

        if (!owned)
        {
            return false;
        }

        var expireAt = GetItemExpireAt(player, item.Id);
        if (expireAt.HasValue && expireAt.Value <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            var wasEnabled = plugin.playerCookies.GetOrDefault(player, EnabledKey(item.Id), false);
            plugin.playerCookies.Set(player, OwnedKey(item.Id), false);
            plugin.playerCookies.Set(player, EnabledKey(item.Id), false);
            plugin.playerCookies.Unset(player, ExpireAtKey(item.Id));
            plugin.playerCookies.Save(player);

            if (wasEnabled)
            {
                OnItemToggled?.Invoke(player, item, false);
            }
            OnItemExpired?.Invoke(player, item);
            if (notifyExpiration)
            {
                plugin.SendLocalizedChat(player, "shop.item.expired", item.DisplayName);
            }

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

        if (!IsItemOwnedInternal(player, item, notifyExpiration: true))
        {
            return false;
        }

        var currentEnabled = plugin.playerCookies.GetOrDefault(player, EnabledKey(item.Id), false);
        if (currentEnabled == enabled)
        {
            return true;
        }

        if (RunBeforeToggleHook(player, item, enabled))
        {
            return false;
        }

        plugin.playerCookies.Set(player, EnabledKey(item.Id), enabled);

        if (enabled && item.Duration.HasValue)
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

    public IReadOnlyCollection<ShopLedgerEntry> GetRecentLedgerEntries(int maxEntries = 100)
    {
        IShopLedgerStore current;
        lock (ledgerStoreSync)
        {
            current = ledgerStore;
        }

        return current.GetRecent(maxEntries);
    }

    public IReadOnlyCollection<ShopLedgerEntry> GetRecentLedgerEntriesForPlayer(IPlayer player, int maxEntries = 50)
    {
        if (player is null || !player.IsValid || maxEntries <= 0)
        {
            return Array.Empty<ShopLedgerEntry>();
        }

        IShopLedgerStore current;
        lock (ledgerStoreSync)
        {
            current = ledgerStore;
        }

        return current.GetRecentForSteamId(player.SteamID, maxEntries);
    }

    private static string NormalizeItemId(string itemId) => itemId.Trim().ToLowerInvariant();
    private static string OwnedKey(string itemId) => $"{CookiePrefix}:owned:{NormalizeItemId(itemId)}";
    private static string EnabledKey(string itemId) => $"{CookiePrefix}:enabled:{NormalizeItemId(itemId)}";
    private static string ExpireAtKey(string itemId) => $"{CookiePrefix}:expireat:{NormalizeItemId(itemId)}";

    private void RecordLedgerEntry(IPlayer player, string action, decimal amount, decimal balanceAfter, ShopItemDefinition? item = null)
    {
        if (player is null || !player.IsValid)
        {
            return;
        }

        var entry = new ShopLedgerEntry(
            TimestampUnixSeconds: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            SteamId: player.SteamID,
            PlayerId: player.PlayerID,
            PlayerName: ResolvePlayerName(player),
            Action: action,
            Amount: amount,
            BalanceAfter: balanceAfter,
            ItemId: item?.Id,
            ItemDisplayName: item?.DisplayName
        );

        try
        {
            IShopLedgerStore current;
            lock (ledgerStoreSync)
            {
                current = ledgerStore;
            }

            current.Record(entry);
        }
        catch (Exception ex)
        {
            plugin.LogWarning(ex, "Failed to persist ledger entry for action '{Action}'.", action);
        }

        OnLedgerEntryRecorded?.Invoke(entry);
    }

    private static string ResolvePlayerName(IPlayer player)
    {
        try
        {
            var name = player.Controller.PlayerName;
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }
        catch
        {
        }

        return $"#{player.PlayerID}";
    }

    private IShopLedgerStore CreateLedgerStore(LedgerConfig config, string pluginDataDirectory)
    {
        if (!config.Enabled)
        {
            return new InMemoryShopLedgerStore(config.MaxInMemoryEntries);
        }

        if (!config.Persistence.Enabled)
        {
            return new InMemoryShopLedgerStore(config.MaxInMemoryEntries);
        }

        if (!string.Equals(config.Persistence.Provider, "sqlite", StringComparison.OrdinalIgnoreCase))
        {
            plugin.LogWarning(
                "Unsupported ledger persistence provider '{Provider}'. Falling back to in-memory ledger.",
                config.Persistence.Provider
            );
            return new InMemoryShopLedgerStore(config.MaxInMemoryEntries);
        }

        try
        {
            var connectionString = ResolveSqliteConnectionString(config.Persistence.ConnectionString, pluginDataDirectory);
            return new FreeSqlShopLedgerStore(connectionString, config.Persistence.AutoSyncStructure);
        }
        catch (Exception ex)
        {
            plugin.LogWarning(
                ex,
                "Failed to initialize FreeSql ledger store. Falling back to in-memory ledger."
            );
            return new InMemoryShopLedgerStore(config.MaxInMemoryEntries);
        }
    }

    private static string ResolveSqliteConnectionString(string configuredValue, string pluginDataDirectory)
    {
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            var defaultPath = Path.Combine(pluginDataDirectory, "shopcore_ledger.sqlite3");
            return $"Data Source={defaultPath}";
        }

        var value = configuredValue.Trim();
        if (!value.Contains('='))
        {
            var path = Path.IsPathRooted(value) ? value : Path.Combine(pluginDataDirectory, value);
            return $"Data Source={path}";
        }

        return value;
    }

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

    private bool TryRunBeforePurchaseHook(IPlayer player, ShopItemDefinition item, out ShopTransactionResult result)
    {
        var context = new ShopBeforePurchaseContext(player, item);

        try
        {
            OnBeforeItemPurchase?.Invoke(context);
        }
        catch (Exception ex)
        {
            plugin.LogWarning(ex, "OnBeforeItemPurchase hook failed for item '{ItemId}'.", item.Id);
        }

        return TryResolveBlockedHook(context, item, out result);
    }

    private bool TryRunBeforeSellHook(IPlayer player, ShopItemDefinition item, out ShopTransactionResult result)
    {
        var context = new ShopBeforeSellContext(player, item);

        try
        {
            OnBeforeItemSell?.Invoke(context);
        }
        catch (Exception ex)
        {
            plugin.LogWarning(ex, "OnBeforeItemSell hook failed for item '{ItemId}'.", item.Id);
        }

        return TryResolveBlockedHook(context, item, out result);
    }

    private bool RunBeforeToggleHook(IPlayer player, ShopItemDefinition item, bool targetEnabled)
    {
        var context = new ShopBeforeToggleContext(player, item, targetEnabled);

        try
        {
            OnBeforeItemToggle?.Invoke(context);
        }
        catch (Exception ex)
        {
            plugin.LogWarning(ex, "OnBeforeItemToggle hook failed for item '{ItemId}'.", item.Id);
        }

        if (!context.IsBlocked)
        {
            return false;
        }

        SendBlockedMessage(context);
        return true;
    }

    private bool TryResolveBlockedHook(ShopBeforeActionContext context, ShopItemDefinition item, out ShopTransactionResult result)
    {
        if (!context.IsBlocked)
        {
            result = default!;
            return false;
        }

        SendBlockedMessage(context);
        var message = string.IsNullOrWhiteSpace(context.Message) ? "Action blocked by module." : context.Message;
        result = new ShopTransactionResult(
            Status: ShopTransactionStatus.BlockedByModule,
            Message: message,
            Item: item
        );
        return true;
    }

    private void SendBlockedMessage(ShopBeforeActionContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.TranslationKey))
        {
            plugin.SendLocalizedChat(context.Player, context.TranslationKey, context.TranslationArgs);
            return;
        }

        if (!string.IsNullOrWhiteSpace(context.Message))
        {
            plugin.SendChatRaw(context.Player, context.Message);
        }
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
