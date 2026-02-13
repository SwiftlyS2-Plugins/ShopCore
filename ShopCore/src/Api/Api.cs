using System.Text.Json;
using System.Reflection;
using Cookies.Contract;
using Economy.Contract;
using FreeSql;
using ShopCore.Contract;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Database;
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
    private readonly object previewCooldownSync = new();
    private readonly Dictionary<string, ShopItemDefinition> itemsById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> categoryToIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> knownModulePluginIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> moduleConfigFileOwners = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ulong, long> previewCooldownUntilUnixMs = new();
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
    public event Action<IPlayer, ShopItemDefinition>? OnItemPreview;
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

    public T LoadModuleConfig<T>(
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
            var normalizedFileName = NormalizeRelativeConfigPath(effectiveFileName);
            if (normalizedFileName is null)
            {
                plugin.LogWarning(
                    "Rejected module config load due to invalid relative config path '{FileName}'. Module='{ModulePluginId}'.",
                    effectiveFileName,
                    modulePluginId
                );
                return new T();
            }
            TrackModuleConfigOwnership(trimmedModulePluginId, normalizedFileName);

            var centralizedConfigPath = plugin.BuildCentralModuleConfigPath(trimmedModulePluginId, normalizedFileName);
            var legacyModuleScopedConfigPath = plugin.BuildLegacyModuleScopedCentralModuleConfigPath(trimmedModulePluginId, normalizedFileName);

            EnsureCentralizedConfig(
                modulePluginId,
                centralizedConfigPath,
                legacyModuleScopedConfigPath
            );

            if (!File.Exists(centralizedConfigPath))
            {
                CreateFallbackCentralizedConfig<T>(modulePluginId, centralizedConfigPath, sectionName);
            }

            if (!File.Exists(centralizedConfigPath))
            {
                plugin.LogDebug(
                    "Centralized module config not found for module '{ModulePluginId}'. Expected path: {ConfigPath}",
                    modulePluginId,
                    centralizedConfigPath
                );
                return new T();
            }

            var rawText = File.ReadAllText(centralizedConfigPath);
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
                "Failed loading module config. Module='{ModulePluginId}', File='{FileName}', Section='{SectionName}'.",
                modulePluginId,
                effectiveFileName,
                sectionName
            );
            return new T();
        }
    }

    public bool SaveModuleConfig<T>(
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
            var normalizedFileName = NormalizeRelativeConfigPath(effectiveFileName);
            if (normalizedFileName is null)
            {
                plugin.LogWarning(
                    "Rejected module config save due to invalid relative config path '{FileName}'. Module='{ModulePluginId}'.",
                    effectiveFileName,
                    modulePluginId
                );
                return false;
            }
            TrackModuleConfigOwnership(modulePluginId.Trim(), normalizedFileName);

            var centralizedConfigPath = plugin.BuildCentralModuleConfigPath(modulePluginId.Trim(), normalizedFileName);

            if (!overwrite && File.Exists(centralizedConfigPath))
            {
                return false;
            }

            var directory = Path.GetDirectoryName(centralizedConfigPath);
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
            File.WriteAllText(centralizedConfigPath, serialized);

            plugin.LogDebug(
                "Saved centralized module config for '{ModulePluginId}' at '{ConfigPath}'.",
                modulePluginId,
                centralizedConfigPath
            );

            return true;
        }
        catch (Exception ex)
        {
            plugin.LogWarning(
                ex,
                "Failed saving module config. Module='{ModulePluginId}', File='{FileName}', Section='{SectionName}'.",
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

    private void EnsureCentralizedConfig(
        string modulePluginId,
        string centralizedConfigPath,
        params string?[] legacyCentralizedConfigPaths)
    {
        if (File.Exists(centralizedConfigPath))
        {
            return;
        }

        foreach (var legacyPath in legacyCentralizedConfigPaths)
        {
            if (string.IsNullOrWhiteSpace(legacyPath) || !File.Exists(legacyPath))
            {
                continue;
            }

            var destinationDirectory = Path.GetDirectoryName(centralizedConfigPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            File.Copy(legacyPath, centralizedConfigPath, overwrite: false);
            plugin.LogDebug(
                "Migrated legacy centralized module config for '{ModulePluginId}' from '{LegacyPath}' to '{ConfigPath}'.",
                modulePluginId,
                legacyPath,
                centralizedConfigPath
            );
            return;
        }
    }

    private void CreateFallbackCentralizedConfig<T>(string modulePluginId, string centralizedConfigPath, string sectionName) where T : class, new()
    {
        var directory = Path.GetDirectoryName(centralizedConfigPath);
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
        File.WriteAllText(centralizedConfigPath, serialized);

        plugin.LogDebug(
            "Created fallback centralized module config for '{ModulePluginId}' at '{ConfigPath}'.",
            modulePluginId,
            centralizedConfigPath
        );
    }

    private static string? NormalizeRelativeConfigPath(string fileName)
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

        var leafName = segments.Length == 0 ? string.Empty : segments[^1];
        return string.IsNullOrWhiteSpace(leafName) ? null : leafName;
    }

    private void TrackModuleConfigOwnership(string modulePluginId, string normalizedFileName)
    {
        lock (knownModulesSync)
        {
            if (moduleConfigFileOwners.TryGetValue(normalizedFileName, out var existingOwner))
            {
                if (!existingOwner.Equals(modulePluginId, StringComparison.OrdinalIgnoreCase))
                {
                    plugin.LogWarning(
                        "Module config file name collision detected for '{FileName}'. Existing owner='{ExistingOwner}', requested by='{RequestedOwner}'. Use unique file names per module.",
                        normalizedFileName,
                        existingOwner,
                        modulePluginId
                    );
                }

                return;
            }

            moduleConfigFileOwners[normalizedFileName] = modulePluginId;
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

        var tracksOwnership = item.IsEquipable && item.Type != ShopItemType.Consumable;
        if (tracksOwnership && IsItemOwned(player, item.Id))
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
        if (tracksOwnership)
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

    public bool PreviewItem(IPlayer player, string itemId)
    {
        if (player is null || !player.IsValid || string.IsNullOrWhiteSpace(itemId))
        {
            return false;
        }

        if (!TryGetItem(itemId, out var item))
        {
            plugin.SendLocalizedChat(player, "shop.error.item_not_found", itemId);
            return false;
        }

        if (!item.Enabled)
        {
            plugin.SendLocalizedChat(player, "shop.error.item_disabled", item.DisplayName);
            return false;
        }

        if (!IsTeamAllowed(player, item.Team))
        {
            plugin.SendLocalizedChat(player, "shop.error.team_not_allowed", item.DisplayName);
            return false;
        }

        if (!item.AllowPreview)
        {
            return false;
        }

        if (IsPreviewOnCooldown(player, out var remainingSeconds))
        {
            plugin.SendLocalizedChat(player, "shop.preview.cooldown", remainingSeconds);
            return false;
        }

        var handlers = OnItemPreview;
        if (handlers is null)
        {
            plugin.SendLocalizedChat(player, "shop.preview.unavailable", item.DisplayName);
            return false;
        }

        var invoked = false;
        foreach (Action<IPlayer, ShopItemDefinition> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(player, item);
                invoked = true;
            }
            catch (Exception ex)
            {
                plugin.LogWarning(ex, "OnItemPreview hook failed for item '{ItemId}'.", item.Id);
            }
        }

        if (!invoked)
        {
            plugin.SendLocalizedChat(player, "shop.preview.unavailable", item.DisplayName);
            return false;
        }

        MarkPreviewCooldown(player);
        return true;
    }

    private bool IsPreviewOnCooldown(IPlayer player, out int remainingSeconds)
    {
        remainingSeconds = 0;

        var cooldownSeconds = plugin.Settings.Behavior.PreviewCooldownSeconds;
        if (cooldownSeconds <= 0f)
        {
            return false;
        }

        if (player.SteamID == 0)
        {
            return false;
        }

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (previewCooldownSync)
        {
            if (!previewCooldownUntilUnixMs.TryGetValue(player.SteamID, out var untilMs))
            {
                return false;
            }

            if (untilMs <= nowMs)
            {
                previewCooldownUntilUnixMs.Remove(player.SteamID);
                return false;
            }

            remainingSeconds = Math.Max(1, (int)Math.Ceiling((untilMs - nowMs) / 1000.0));
            return true;
        }
    }

    private void MarkPreviewCooldown(IPlayer player)
    {
        var cooldownSeconds = plugin.Settings.Behavior.PreviewCooldownSeconds;
        if (cooldownSeconds <= 0f || player.SteamID == 0)
        {
            return;
        }

        var untilMs = DateTimeOffset.UtcNow.AddSeconds(cooldownSeconds).ToUnixTimeMilliseconds();
        lock (previewCooldownSync)
        {
            previewCooldownUntilUnixMs[player.SteamID] = untilMs;
        }
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

        if (!item.IsEquipable)
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
        if (!item.IsEquipable)
        {
            var ownedKey = OwnedKey(item.Id);
            var enabledKey = EnabledKey(item.Id);
            var expireKey = ExpireAtKey(item.Id);

            var hadOwned = plugin.playerCookies.GetOrDefault(player, ownedKey, false);
            var hadEnabled = plugin.playerCookies.GetOrDefault(player, enabledKey, false);
            var hadExpireAt = plugin.playerCookies.GetOrDefault(player, expireKey, 0L) > 0L;

            if (hadOwned || hadEnabled || hadExpireAt)
            {
                plugin.playerCookies.Set(player, ownedKey, false);
                plugin.playerCookies.Set(player, enabledKey, false);
                plugin.playerCookies.Unset(player, expireKey);
                plugin.playerCookies.Save(player);
            }

            return false;
        }

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

        if (!item.IsEquipable)
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
        plugin.SendLocalizedChat(
            player,
            enabled ? "shop.item.equipped" : "shop.item.unequipped",
            item.DisplayName
        );
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

        try
        {
            var connectionName = string.IsNullOrWhiteSpace(config.Persistence.ConnectionName)
                ? "default"
                : config.Persistence.ConnectionName.Trim();
            var databaseInfo = TryGetDatabaseConnectionInfo(connectionName);

            if (!TryResolvePersistenceProvider(config.Persistence.Provider, databaseInfo, out var dataType, out var providerName))
            {
                plugin.LogWarning(
                    "Unsupported ledger persistence provider '{Provider}'. Falling back to in-memory ledger.",
                    config.Persistence.Provider
                );
                return new InMemoryShopLedgerStore(config.MaxInMemoryEntries);
            }

            var connectionString = ResolvePersistenceConnectionString(
                dataType,
                config.Persistence.ConnectionString,
                pluginDataDirectory,
                databaseInfo
            );

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                plugin.LogWarning(
                    "Unable to resolve {Provider} connection string for ledger persistence. Falling back to in-memory ledger.",
                    providerName
                );
                return new InMemoryShopLedgerStore(config.MaxInMemoryEntries);
            }

            return new FreeSqlShopLedgerStore(dataType, connectionString, config.Persistence.AutoSyncStructure);
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

    private DatabaseConnectionInfo? TryGetDatabaseConnectionInfo(string connectionName)
    {
        return plugin.TryGetDatabaseConnectionInfo(connectionName);
    }

    private static bool TryResolvePersistenceProvider(
        string configuredProvider,
        DatabaseConnectionInfo? databaseInfo,
        out DataType dataType,
        out string providerName)
    {
        var provider = configuredProvider?.Trim().ToLowerInvariant() ?? string.Empty;
        if (provider is "sqlite" or "sqlite3")
        {
            dataType = DataType.Sqlite;
            providerName = "sqlite";
            return true;
        }

        if (provider is "mysql" or "mariadb")
        {
            dataType = DataType.MySql;
            providerName = "mysql";
            return true;
        }

        if (provider is "" or "auto")
        {
            if (databaseInfo.HasValue && TryMapDriverToDataType(databaseInfo.Value.Driver, out dataType, out providerName))
            {
                return true;
            }

            dataType = DataType.Sqlite;
            providerName = "sqlite";
            return true;
        }

        dataType = default;
        providerName = string.Empty;
        return false;
    }

    private static string ResolvePersistenceConnectionString(
        DataType dataType,
        string configuredValue,
        string pluginDataDirectory,
        DatabaseConnectionInfo? databaseInfo)
    {
        if (!string.IsNullOrWhiteSpace(configuredValue))
        {
            return ResolveConfiguredConnectionString(dataType, configuredValue, pluginDataDirectory);
        }

        if (databaseInfo.HasValue && TryMapDriverToDataType(databaseInfo.Value.Driver, out var driverDataType, out _))
        {
            if (driverDataType == dataType)
            {
                return databaseInfo.Value.ToString();
            }
        }

        return dataType switch
        {
            DataType.Sqlite => $"Data Source={Path.Combine(pluginDataDirectory, "shopcore_ledger.sqlite3")}",
            _ => string.Empty
        };
    }

    private static string ResolveConfiguredConnectionString(DataType dataType, string configuredValue, string pluginDataDirectory)
    {
        var value = ExpandPathTokens(configuredValue.Trim(), pluginDataDirectory);

        if (dataType != DataType.Sqlite)
        {
            return value;
        }

        if (!value.Contains('=') && !value.Contains("://", StringComparison.Ordinal))
        {
            var path = Path.IsPathRooted(value) ? value : Path.Combine(pluginDataDirectory, value);
            return $"Data Source={path}";
        }

        return value;
    }

    private static string ExpandPathTokens(string value, string pluginDataDirectory)
    {
        return value
            .Replace("${PluginDataDirectory}", pluginDataDirectory, StringComparison.OrdinalIgnoreCase)
            .Replace("$(PluginDataDirectory)", pluginDataDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryMapDriverToDataType(string? driver, out DataType dataType, out string providerName)
    {
        var normalizedDriver = driver?.Trim().ToLowerInvariant();
        switch (normalizedDriver)
        {
            case "sqlite":
                dataType = DataType.Sqlite;
                providerName = "sqlite";
                return true;
            case "mysql":
            case "mariadb":
                dataType = DataType.MySql;
                providerName = "mysql";
                return true;
            default:
                dataType = default;
                providerName = string.Empty;
                return false;
        }
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
        var blockedBy = InvokeBeforePurchaseHooks(context, item.Id);
        return TryResolveBlockedHook(context, item, blockedBy, out result);
    }

    private bool TryRunBeforeSellHook(IPlayer player, ShopItemDefinition item, out ShopTransactionResult result)
    {
        var context = new ShopBeforeSellContext(player, item);
        var blockedBy = InvokeBeforeSellHooks(context, item.Id);
        return TryResolveBlockedHook(context, item, blockedBy, out result);
    }

    private bool RunBeforeToggleHook(IPlayer player, ShopItemDefinition item, bool targetEnabled)
    {
        var context = new ShopBeforeToggleContext(player, item, targetEnabled);
        var blockedBy = InvokeBeforeToggleHooks(context, item.Id);

        if (!context.IsBlocked)
        {
            return false;
        }

        SendBlockedMessage(context, blockedBy);
        return true;
    }

    private object? InvokeBeforePurchaseHooks(ShopBeforePurchaseContext context, string itemId)
    {
        var handlers = OnBeforeItemPurchase;
        if (handlers is null)
        {
            return null;
        }

        foreach (Action<ShopBeforePurchaseContext> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(context);
            }
            catch (Exception ex)
            {
                plugin.LogWarning(ex, "OnBeforeItemPurchase hook failed for item '{ItemId}'.", itemId);
            }

            if (context.IsBlocked)
            {
                return handler.Target;
            }
        }

        return null;
    }

    private object? InvokeBeforeSellHooks(ShopBeforeSellContext context, string itemId)
    {
        var handlers = OnBeforeItemSell;
        if (handlers is null)
        {
            return null;
        }

        foreach (Action<ShopBeforeSellContext> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(context);
            }
            catch (Exception ex)
            {
                plugin.LogWarning(ex, "OnBeforeItemSell hook failed for item '{ItemId}'.", itemId);
            }

            if (context.IsBlocked)
            {
                return handler.Target;
            }
        }

        return null;
    }

    private object? InvokeBeforeToggleHooks(ShopBeforeToggleContext context, string itemId)
    {
        var handlers = OnBeforeItemToggle;
        if (handlers is null)
        {
            return null;
        }

        foreach (Action<ShopBeforeToggleContext> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(context);
            }
            catch (Exception ex)
            {
                plugin.LogWarning(ex, "OnBeforeItemToggle hook failed for item '{ItemId}'.", itemId);
            }

            if (context.IsBlocked)
            {
                return handler.Target;
            }
        }

        return null;
    }

    private bool TryResolveBlockedHook(ShopBeforeActionContext context, ShopItemDefinition item, object? blockedBy, out ShopTransactionResult result)
    {
        if (!context.IsBlocked)
        {
            result = default!;
            return false;
        }

        SendBlockedMessage(context, blockedBy);
        var message = string.IsNullOrWhiteSpace(context.Message) ? "Action blocked by module." : context.Message;
        result = new ShopTransactionResult(
            Status: ShopTransactionStatus.BlockedByModule,
            Message: message,
            Item: item
        );
        return true;
    }

    private void SendBlockedMessage(ShopBeforeActionContext context, object? blockedBy)
    {
        if (!string.IsNullOrWhiteSpace(context.TranslationKey))
        {
            if (TrySendBlockedMessageWithModuleLocalizer(context, blockedBy))
            {
                return;
            }

            plugin.SendLocalizedChat(context.Player, context.TranslationKey, context.TranslationArgs);
            return;
        }

        if (!string.IsNullOrWhiteSpace(context.Message))
        {
            plugin.SendChatRaw(context.Player, context.Message);
        }
    }

    private bool TrySendBlockedMessageWithModuleLocalizer(ShopBeforeActionContext context, object? blockedBy)
    {
        if (blockedBy is null || string.IsNullOrWhiteSpace(context.TranslationKey))
        {
            return false;
        }

        try
        {
            var coreProperty = blockedBy.GetType().GetProperty("Core", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (coreProperty?.GetValue(blockedBy) is not ISwiftlyCore moduleCore)
            {
                return false;
            }

            var localized = moduleCore.Localizer[context.TranslationKey, context.TranslationArgs];
            if (string.IsNullOrWhiteSpace(localized) || string.Equals(localized, context.TranslationKey, StringComparison.Ordinal))
            {
                return false;
            }

            var prefix = moduleCore.Localizer["shop.prefix"];
            var message = string.IsNullOrWhiteSpace(prefix) || string.Equals(prefix, "shop.prefix", StringComparison.Ordinal)
                ? localized
                : $"{prefix} {localized}";

            plugin.SendChatRaw(context.Player, message);
            return true;
        }
        catch (Exception ex)
        {
            plugin.LogDebug(
                "Failed to resolve blocked message from module localizer for key '{TranslationKey}'. Error={Error}",
                context.TranslationKey,
                ex.Message
            );
            return false;
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


