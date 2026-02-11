using Microsoft.Extensions.Logging;
using ShopCore.Contract;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Plugins;

namespace ShopCore;

[PluginMetadata(
    Id = "Shop_Flags",
    Name = "Shop Flags",
    Author = "T3Marius",
    Version = "1.0.0",
    Description = "ShopCore module with permission flag items"
)]
public class Shop_Flags : BasePlugin
{
    private const string ShopCoreInterfaceKey = "ShopCore.API.v1";
    private const string ModulePluginId = "Shop_Flags";
    private const string TemplateFileName = "flags_config.jsonc";
    private const string TemplateSectionName = "Main";
    private const string DefaultCategory = "Permissions/Flags";

    private IShopCoreApiV1? shopApi;
    private bool handlersRegistered;
    private readonly HashSet<string> registeredItemIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FlagItemRuntime> itemRuntimeById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ulong, HashSet<string>> activePermissionsBySteam = new();
    private readonly Dictionary<int, ulong> steamByPlayerId = new();

    public Shop_Flags(ISwiftlyCore core) : base(core) { }

    public override void UseSharedInterface(IInterfaceManager interfaceManager)
    {
        shopApi = null;

        if (!interfaceManager.HasSharedInterface(ShopCoreInterfaceKey))
        {
            return;
        }

        try
        {
            shopApi = interfaceManager.GetSharedInterface<IShopCoreApiV1>(ShopCoreInterfaceKey);
        }
        catch (Exception ex)
        {
            Core.Logger.LogInformation(ex, "Failed to resolve shared interface '{InterfaceKey}'.", ShopCoreInterfaceKey);
        }
    }

    public override void OnSharedInterfaceInjected(IInterfaceManager interfaceManager)
    {
        if (shopApi is null)
        {
            Core.Logger.LogWarning("ShopCore API is not available. Flags items will not be registered.");
            return;
        }

        if (!handlersRegistered)
        {
            RegisterItemsAndHandlers();
        }
    }

    public override void Load(bool hotReload)
    {
        Core.Event.OnClientConnected += OnClientConnected;
        Core.Event.OnClientDisconnected += OnClientDisconnected;

        if (shopApi is not null && !handlersRegistered)
        {
            RegisterItemsAndHandlers();
        }
    }

    public override void Unload()
    {
        Core.Event.OnClientConnected -= OnClientConnected;
        Core.Event.OnClientDisconnected -= OnClientDisconnected;

        RunOnMainThread(RemoveAllTrackedPermissions);
        UnregisterItemsAndHandlers();
    }

    private void RegisterItemsAndHandlers()
    {
        if (shopApi is null)
        {
            return;
        }

        UnregisterItemsAndHandlers();

        var moduleConfig = shopApi.LoadModuleConfig<FlagsModuleConfig>(
            ModulePluginId,
            TemplateFileName,
            TemplateSectionName
        );
        NormalizeConfig(moduleConfig);

        var category = string.IsNullOrWhiteSpace(moduleConfig.Settings.Category)
            ? DefaultCategory
            : moduleConfig.Settings.Category.Trim();

        if (moduleConfig.Items.Count == 0)
        {
            moduleConfig = CreateDefaultConfig();
            category = moduleConfig.Settings.Category;
            _ = shopApi.SaveModuleConfig(
                ModulePluginId,
                moduleConfig,
                TemplateFileName,
                TemplateSectionName,
                overwrite: true
            );
        }

        var registeredCount = 0;
        foreach (var itemTemplate in moduleConfig.Items)
        {
            if (!TryCreateDefinition(itemTemplate, category, out var definition, out var runtime))
            {
                continue;
            }

            if (!shopApi.RegisterItem(definition))
            {
                Core.Logger.LogWarning("Failed to register flag item '{ItemId}'.", definition.Id);
                continue;
            }

            _ = registeredItemIds.Add(definition.Id);
            itemRuntimeById[definition.Id] = runtime;
            registeredCount++;
        }

        shopApi.OnBeforeItemPurchase += OnBeforeItemPurchase;
        shopApi.OnItemPurchased += OnItemPurchased;
        shopApi.OnItemToggled += OnItemToggled;
        shopApi.OnItemSold += OnItemSold;
        shopApi.OnItemExpired += OnItemExpired;
        handlersRegistered = true;

        RunOnMainThread(SyncAllOnlinePlayers);

        Core.Logger.LogInformation(
            "Shop_Flags initialized. RegisteredItems={RegisteredItems}",
            registeredCount
        );
    }

    private void UnregisterItemsAndHandlers()
    {
        if (!handlersRegistered || shopApi is null)
        {
            return;
        }

        shopApi.OnBeforeItemPurchase -= OnBeforeItemPurchase;
        shopApi.OnItemPurchased -= OnItemPurchased;
        shopApi.OnItemToggled -= OnItemToggled;
        shopApi.OnItemSold -= OnItemSold;
        shopApi.OnItemExpired -= OnItemExpired;

        foreach (var itemId in registeredItemIds)
        {
            _ = shopApi.UnregisterItem(itemId);
        }

        registeredItemIds.Clear();
        itemRuntimeById.Clear();
        handlersRegistered = false;
    }

    private void OnBeforeItemPurchase(ShopBeforePurchaseContext context)
    {
        if (!registeredItemIds.Contains(context.Item.Id))
        {
            return;
        }

        if (!itemRuntimeById.TryGetValue(context.Item.Id, out var runtime))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(runtime.RequiredPermission))
        {
            return;
        }

        if (Core.Permission.PlayerHasPermission(context.Player.SteamID, runtime.RequiredPermission))
        {
            return;
        }

        context.BlockLocalized(
            "module.flags.error.permission",
            context.Item.DisplayName,
            runtime.RequiredPermission
        );
    }

    private void OnItemPurchased(IPlayer player, ShopItemDefinition item)
    {
        if (!registeredItemIds.Contains(item.Id))
        {
            return;
        }

        RunOnMainThread(() => SyncPlayerPermissions(player));
    }

    private void OnItemToggled(IPlayer player, ShopItemDefinition item, bool enabled)
    {
        if (!registeredItemIds.Contains(item.Id))
        {
            return;
        }

        RunOnMainThread(() => SyncPlayerPermissions(player));
    }

    private void OnItemSold(IPlayer player, ShopItemDefinition item, decimal creditedAmount)
    {
        if (!registeredItemIds.Contains(item.Id))
        {
            return;
        }

        RunOnMainThread(() => SyncPlayerPermissions(player));
    }

    private void OnItemExpired(IPlayer player, ShopItemDefinition item)
    {
        if (!registeredItemIds.Contains(item.Id))
        {
            return;
        }

        RunOnMainThread(() => SyncPlayerPermissions(player));
    }

    private void OnClientConnected(IOnClientConnectedEvent @event)
    {
        RunOnMainThread(() =>
        {
            var player = Core.PlayerManager.GetPlayer(@event.PlayerId);
            if (player is null || !player.IsValid || player.IsFakeClient)
            {
                return;
            }

            SyncPlayerPermissions(player);
        });
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        RunOnMainThread(() =>
        {
            if (!steamByPlayerId.TryGetValue(@event.PlayerId, out var steamId))
            {
                return;
            }

            RemoveTrackedPermissions(steamId);
            steamByPlayerId.Remove(@event.PlayerId);
        });
    }

    private void SyncAllOnlinePlayers()
    {
        foreach (var player in Core.PlayerManager.GetAllValidPlayers())
        {
            if (player.IsFakeClient)
            {
                continue;
            }

            SyncPlayerPermissions(player);
        }
    }

    private void SyncPlayerPermissions(IPlayer player)
    {
        if (shopApi is null || !player.IsValid || player.IsFakeClient)
        {
            return;
        }

        steamByPlayerId[player.PlayerID] = player.SteamID;

        var desiredPermissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var itemId in registeredItemIds)
        {
            if (!shopApi.IsItemEnabled(player, itemId))
            {
                continue;
            }

            if (!itemRuntimeById.TryGetValue(itemId, out var runtime))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(runtime.GrantedPermission))
            {
                continue;
            }

            desiredPermissions.Add(runtime.GrantedPermission);
        }

        if (!activePermissionsBySteam.TryGetValue(player.SteamID, out var activePermissions))
        {
            activePermissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            activePermissionsBySteam[player.SteamID] = activePermissions;
        }

        foreach (var permission in desiredPermissions.ToArray())
        {
            if (activePermissions.Contains(permission))
            {
                continue;
            }

            if (!Core.Permission.PlayerHasPermission(player.SteamID, permission))
            {
                Core.Permission.AddPermission(player.SteamID, permission);
                Core.Logger.LogInformation(
                    "Added permission '{Permission}' to player '{PlayerName}' ({SteamId}) from Shop_Flags.",
                    permission,
                    player.Controller.PlayerName,
                    player.SteamID
                );
            }

            activePermissions.Add(permission);
        }

        foreach (var permission in activePermissions.ToArray())
        {
            if (desiredPermissions.Contains(permission))
            {
                continue;
            }

            if (Core.Permission.PlayerHasPermission(player.SteamID, permission))
            {
                Core.Permission.RemovePermission(player.SteamID, permission);
                Core.Logger.LogInformation(
                    "Removed permission '{Permission}' from player '{PlayerName}' ({SteamId}) from Shop_Flags.",
                    permission,
                    player.Controller.PlayerName,
                    player.SteamID
                );
            }

            activePermissions.Remove(permission);
        }

        if (activePermissions.Count == 0)
        {
            activePermissionsBySteam.Remove(player.SteamID);
        }
    }

    private void RemoveTrackedPermissions(ulong steamId)
    {
        if (!activePermissionsBySteam.TryGetValue(steamId, out var permissions))
        {
            return;
        }

        foreach (var permission in permissions)
        {
            if (!Core.Permission.PlayerHasPermission(steamId, permission))
            {
                continue;
            }

            Core.Permission.RemovePermission(steamId, permission);
        }

        activePermissionsBySteam.Remove(steamId);
    }

    private void RemoveAllTrackedPermissions()
    {
        foreach (var steamId in activePermissionsBySteam.Keys.ToArray())
        {
            RemoveTrackedPermissions(steamId);
        }

        activePermissionsBySteam.Clear();
        steamByPlayerId.Clear();
    }

    private void RunOnMainThread(Action action)
    {
        Core.Scheduler.NextWorldUpdate(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Core.Logger.LogWarning(ex, "Shop_Flags main-thread action failed.");
            }
        });
    }

    private bool TryCreateDefinition(
        FlagItemTemplate itemTemplate,
        string category,
        out ShopItemDefinition definition,
        out FlagItemRuntime runtime)
    {
        definition = default!;
        runtime = default;

        if (string.IsNullOrWhiteSpace(itemTemplate.Id))
        {
            return false;
        }

        var itemId = itemTemplate.Id.Trim();
        if (itemTemplate.Price <= 0)
        {
            Core.Logger.LogWarning("Skipping item '{ItemId}' because Price must be greater than 0.", itemId);
            return false;
        }

        if (string.IsNullOrWhiteSpace(itemTemplate.GrantedPermission))
        {
            Core.Logger.LogWarning("Skipping item '{ItemId}' because GrantedPermission is empty.", itemId);
            return false;
        }

        if (!Enum.TryParse(itemTemplate.Type, ignoreCase: true, out ShopItemType itemType))
        {
            Core.Logger.LogWarning("Skipping item '{ItemId}' because Type '{Type}' is invalid.", itemId, itemTemplate.Type);
            return false;
        }

        if (itemType == ShopItemType.Consumable)
        {
            Core.Logger.LogWarning(
                "Skipping item '{ItemId}' because flag items cannot use Type '{Type}'.",
                itemId,
                itemType
            );
            return false;
        }

        if (!Enum.TryParse(itemTemplate.Team, ignoreCase: true, out ShopItemTeam team))
        {
            team = ShopItemTeam.Any;
        }

        TimeSpan? duration = null;
        if (itemTemplate.DurationSeconds > 0)
        {
            duration = TimeSpan.FromSeconds(itemTemplate.DurationSeconds);
        }

        if (itemType == ShopItemType.Temporary && !duration.HasValue)
        {
            Core.Logger.LogWarning(
                "Skipping item '{ItemId}' because Temporary items require DurationSeconds > 0.",
                itemId
            );
            return false;
        }

        decimal? sellPrice = null;
        if (itemTemplate.SellPrice.HasValue && itemTemplate.SellPrice.Value >= 0)
        {
            sellPrice = itemTemplate.SellPrice.Value;
        }

        definition = new ShopItemDefinition(
            Id: itemId,
            DisplayName: ResolveDisplayName(itemTemplate),
            Category: category,
            Price: itemTemplate.Price,
            SellPrice: sellPrice,
            Duration: duration,
            Type: itemType,
            Team: team,
            Enabled: itemTemplate.Enabled,
            CanBeSold: itemTemplate.CanBeSold
        );

        runtime = new FlagItemRuntime(
            ItemId: itemId,
            GrantedPermission: itemTemplate.GrantedPermission.Trim(),
            RequiredPermission: itemTemplate.RequiredPermission?.Trim() ?? string.Empty
        );

        return true;
    }

    private string ResolveDisplayName(FlagItemTemplate itemTemplate)
    {
        if (!string.IsNullOrWhiteSpace(itemTemplate.DisplayNameKey))
        {
            var key = itemTemplate.DisplayNameKey.Trim();
            var localized = itemTemplate.Type.Equals(nameof(ShopItemType.Permanent), StringComparison.OrdinalIgnoreCase)
                ? Core.Localizer[key]
                : Core.Localizer[key, FormatDuration(itemTemplate.DurationSeconds)];
            if (!string.Equals(localized, key, StringComparison.Ordinal))
            {
                return localized;
            }
        }

        if (!string.IsNullOrWhiteSpace(itemTemplate.DisplayName))
        {
            return itemTemplate.DisplayName.Trim();
        }

        return itemTemplate.Id.Trim();
    }

    private static string FormatDuration(int totalSeconds)
    {
        if (totalSeconds <= 0)
        {
            return "0 Seconds";
        }

        var ts = TimeSpan.FromSeconds(totalSeconds);
        if (ts.TotalHours >= 1)
        {
            var hours = (int)ts.TotalHours;
            var minutes = ts.Minutes;
            return minutes > 0
                ? $"{hours} Hour{(hours == 1 ? "" : "s")} {minutes} Minute{(minutes == 1 ? "" : "s")}"
                : $"{hours} Hour{(hours == 1 ? "" : "s")}";
        }

        if (ts.TotalMinutes >= 1)
        {
            var minutes = (int)ts.TotalMinutes;
            var seconds = ts.Seconds;
            return seconds > 0
                ? $"{minutes} Minute{(minutes == 1 ? "" : "s")} {seconds} Second{(seconds == 1 ? "" : "s")}"
                : $"{minutes} Minute{(minutes == 1 ? "" : "s")}";
        }

        return $"{ts.Seconds} Second{(ts.Seconds == 1 ? "" : "s")}";
    }

    private static void NormalizeConfig(FlagsModuleConfig config)
    {
        config.Settings ??= new FlagsModuleSettings();
        config.Items ??= [];
    }

    private static FlagsModuleConfig CreateDefaultConfig()
    {
        return new FlagsModuleConfig
        {
            Settings = new FlagsModuleSettings
            {
                Category = DefaultCategory
            },
            Items =
            [
                new FlagItemTemplate
                {
                    Id = "flag_slot_hourly",
                    DisplayNameKey = "module.flags.item.slot.name",
                    GrantedPermission = "swiftly.slot",
                    Price = 2500,
                    SellPrice = 1250,
                    DurationSeconds = 3600,
                    Type = nameof(ShopItemType.Temporary),
                    Team = nameof(ShopItemTeam.Any),
                    Enabled = true,
                    CanBeSold = true
                }
            ]
        };
    }
}

internal readonly record struct FlagItemRuntime(
    string ItemId,
    string GrantedPermission,
    string RequiredPermission
);

internal sealed class FlagsModuleConfig
{
    public FlagsModuleSettings Settings { get; set; } = new();
    public List<FlagItemTemplate> Items { get; set; } = [];
}

internal sealed class FlagsModuleSettings
{
    public string Category { get; set; } = "Permissions/Flags";
}

internal sealed class FlagItemTemplate
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string DisplayNameKey { get; set; } = string.Empty;
    public string GrantedPermission { get; set; } = string.Empty;
    public string RequiredPermission { get; set; } = string.Empty;
    public int Price { get; set; } = 0;
    public int? SellPrice { get; set; }
    public int DurationSeconds { get; set; } = 0;
    public string Type { get; set; } = nameof(ShopItemType.Temporary);
    public string Team { get; set; } = nameof(ShopItemTeam.Any);
    public bool Enabled { get; set; } = true;
    public bool CanBeSold { get; set; } = true;
}

