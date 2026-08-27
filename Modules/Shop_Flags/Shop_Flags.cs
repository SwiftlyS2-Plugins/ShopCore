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
    private const string ShopCoreInterfaceKey = "ShopCore.API.v2";
    private const string ModulePluginId = "Shop_Flags";
    private const string TemplateFileName = "flags_config.jsonc";
    private const string TemplateSectionName = "Main";
    private const string DefaultCategory = "Permissions/Flags";

    private IShopCoreApiV2? shopApi;
    private bool handlersRegistered;
    private CancellationTokenSource? _checkTimerCts;
    private readonly HashSet<string> registeredItemIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FlagItemRuntime> itemRuntimeById = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<ulong, HashSet<string>> activePermissionsBySteam = new();
    private FlagsModuleSettings runtimeSettings = new();

    public Shop_Flags(ISwiftlyCore core) : base(core) { }

    public override void UseSharedInterface(IInterfaceManager interfaceManager)
    {
        shopApi = null;
        if (!interfaceManager.HasSharedInterface(ShopCoreInterfaceKey))
            return;

        try
        {
            shopApi = interfaceManager.GetSharedInterface<IShopCoreApiV2>(ShopCoreInterfaceKey);
        }
        catch (Exception ex)
        {
            Core.Logger.LogError(ex, "Failed to resolve shared interface '{InterfaceKey}'.", ShopCoreInterfaceKey);
        }
    }

    public override void OnSharedInterfaceInjected(IInterfaceManager interfaceManager)
    {
        if (shopApi == null)
            return;

        RegisterItemsAndHandlers();
    }

    public override void Load(bool hotReload)
    {
        Core.Event.OnClientDisconnected += OnClientDisconnected;

        if (shopApi is not null && !handlersRegistered)
            RegisterItemsAndHandlers();

        _checkTimerCts?.Cancel();
        _checkTimerCts = Core.Scheduler.RepeatBySeconds(5f, () => CheckAllPlayers());
    }

    public override void Unload()
    {
        _checkTimerCts?.Cancel();
        Core.Event.OnClientDisconnected -= OnClientDisconnected;

        RunOnMainThread(RemoveAllTrackedPermissions);
        UnregisterItemsAndHandlers();
    }

    private void CheckAllPlayers()
    {
        if (shopApi == null) return;

        for (var i = 0; i < Core.PlayerManager.PlayerCap; i++)
        {
            var player = Core.PlayerManager.GetPlayer(i);
            if (player == null || player.IsFakeClient || !player.IsValid) continue;

            SyncPlayerPermissions(player);
        }
    }

    private void RegisterItemsAndHandlers()
    {
        if (shopApi == null)
            return;

        UnregisterItemsAndHandlers();

        var moduleConfig = shopApi.LoadModuleConfig<FlagsModuleConfig>(
            ModulePluginId,
            TemplateFileName,
            TemplateSectionName
        );

        NormalizeConfig(moduleConfig);
        runtimeSettings = moduleConfig.Settings;

        var category = string.IsNullOrWhiteSpace(moduleConfig.Settings.Category)
            ? DefaultCategory
            : moduleConfig.Settings.Category.Trim();

        if (moduleConfig.Items.Count == 0)
        {
            moduleConfig = CreateDefaultConfig();
            category = moduleConfig.Settings.Category;
            runtimeSettings = moduleConfig.Settings;

            _ = shopApi.SaveModuleConfig(
                ModulePluginId,
                moduleConfig,
                TemplateFileName,
                TemplateSectionName,
                overwrite: true
            );
        }

        foreach (var itemTemplate in moduleConfig.Items)
        {
            if (!TryCreateDefinition(itemTemplate, category, out var definition, out var runtime))
                continue;

            if (!shopApi.RegisterItem(definition))
                continue;

            _ = registeredItemIds.Add(definition.Id);
            itemRuntimeById[definition.Id] = runtime;
        }

        shopApi.OnBeforeItemPurchase += OnBeforeItemPurchase;
        handlersRegistered = true;

        RunOnMainThread(SyncAllOnlinePlayers);
    }

    private void UnregisterItemsAndHandlers()
    {
        if (!handlersRegistered || shopApi == null)
            return;

        shopApi.OnBeforeItemPurchase -= OnBeforeItemPurchase;

        foreach (var itemId in registeredItemIds)
            _ = shopApi.UnregisterItem(itemId);

        registeredItemIds.Clear();
        itemRuntimeById.Clear();
        handlersRegistered = false;
    }

    private void OnBeforeItemPurchase(ShopBeforePurchaseContext context)
    {
        if (!registeredItemIds.Contains(context.Item.Id))
            return;

        if (!itemRuntimeById.TryGetValue(context.Item.Id, out var runtime))
            return;

        if (string.IsNullOrWhiteSpace(runtime.RequiredPermission))
            return;

        if (Core.Permission.PlayerHasPermission(context.Player.SteamID, runtime.RequiredPermission))
            return;

        var player = context.Player;
        var loc = Core.Translation.GetPlayerLocalizer(player);
        context.Block($"{GetPrefix(player)} {loc["error.permission", shopApi?.GetItemDisplayName(player, context.Item) ?? context.Item.DisplayName, runtime.RequiredPermission]}");
    }

    private string GetPrefix(IPlayer player)
    {
        var loc = Core.Translation.GetPlayerLocalizer(player);
        if (runtimeSettings.UseCorePrefix)
        {
            var corePrefix = shopApi?.GetShopPrefix(player);
            if (!string.IsNullOrWhiteSpace(corePrefix))
                return corePrefix;
        }

        return loc["shop.prefix"];
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        RunOnMainThread(() =>
        {
            var player = Core.PlayerManager.GetPlayer(@event.PlayerId);
            if (player == null || player.IsFakeClient) return;

            if (activePermissionsBySteam.TryGetValue(player.SteamID, out var permissions))
            {
                foreach (var permission in permissions)
                {
                    if (Core.Permission.PlayerHasPermission(player.SteamID, permission))
                    {
                        Core.Permission.RemovePermission(player.SteamID, permission);
                    }
                }
                activePermissionsBySteam.Remove(player.SteamID);
            }
        });
    }

    private void SyncAllOnlinePlayers()
    {
        foreach (var player in Core.PlayerManager.GetAllValidPlayers())
        {
            if (player.IsFakeClient || !player.IsValid)
                continue;

            SyncPlayerPermissions(player);
        }
    }

    private void SyncPlayerPermissions(IPlayer player)
    {
        if (shopApi == null || !player.IsValid || player.IsFakeClient)
            return;

        var desiredPermissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var itemId in registeredItemIds)
        {
            if (!shopApi.IsItemEnabled(player, itemId))
                continue;

            if (!itemRuntimeById.TryGetValue(itemId, out var runtime))
                continue;

            if (string.IsNullOrWhiteSpace(runtime.GrantedPermission))
                continue;

            desiredPermissions.Add(runtime.GrantedPermission);
        }

        if (!activePermissionsBySteam.TryGetValue(player.SteamID, out var activePermissions))
        {
            activePermissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            activePermissionsBySteam[player.SteamID] = activePermissions;
        }

        foreach (var permission in desiredPermissions)
        {
            if (!activePermissions.Contains(permission))
            {
                activePermissions.Add(permission);
            }

            if (!Core.Permission.PlayerHasPermission(player.SteamID, permission))
            {
                Core.Permission.AddPermission(player.SteamID, permission);
            }
        }

        foreach (var permission in activePermissions.ToArray())
        {
            if (desiredPermissions.Contains(permission))
                continue;

            if (Core.Permission.PlayerHasPermission(player.SteamID, permission))
            {
                Core.Permission.RemovePermission(player.SteamID, permission);
            }

            activePermissions.Remove(permission);
        }

        if (activePermissions.Count == 0)
            activePermissionsBySteam.Remove(player.SteamID);
    }

    private void RemoveTrackedPermissions(ulong steamId)
    {
        if (!activePermissionsBySteam.TryGetValue(steamId, out var permissions))
            return;

        foreach (var permission in permissions)
        {
            if (!Core.Permission.PlayerHasPermission(steamId, permission))
                continue;

            Core.Permission.RemovePermission(steamId, permission);
        }

        activePermissionsBySteam.Remove(steamId);
    }

    private void RemoveAllTrackedPermissions()
    {
        foreach (var steamId in activePermissionsBySteam.Keys.ToArray())
            RemoveTrackedPermissions(steamId);

        activePermissionsBySteam.Clear();
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

    private bool TryCreateDefinition(FlagItemTemplate itemTemplate, string category, out ShopItemDefinition definition, out FlagItemRuntime runtime)
    {
        definition = default!;
        runtime = default;

        if (string.IsNullOrWhiteSpace(itemTemplate.Id))
            return false;

        var itemId = itemTemplate.Id.Trim();
        if (itemTemplate.Price <= 0 || string.IsNullOrWhiteSpace(itemTemplate.GrantedPermission))
            return false;

        if (!Enum.TryParse(itemTemplate.Type, ignoreCase: true, out ShopItemType itemType) || itemType == ShopItemType.Consumable)
            return false;

        if (!Enum.TryParse(itemTemplate.Team, ignoreCase: true, out ShopItemTeam team))
            team = ShopItemTeam.Any;

        TimeSpan? duration = null;
        if (itemTemplate.DurationSeconds > 0)
            duration = TimeSpan.FromSeconds(itemTemplate.DurationSeconds);

        if (itemType == ShopItemType.Temporary && !duration.HasValue)
            return false;

        decimal? sellPrice = itemTemplate.SellPrice is >= 0 ? itemTemplate.SellPrice.Value : null;

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
            CanBeSold: itemTemplate.CanBeSold,
            DisplayNameResolver: player => ResolveDisplayName(itemTemplate, player)
        );

        runtime = new FlagItemRuntime(
            ItemId: itemId,
            GrantedPermission: itemTemplate.GrantedPermission.Trim(),
            RequiredPermission: itemTemplate.RequiredPermission?.Trim() ?? string.Empty
        );

        return true;
    }

    private string ResolveDisplayName(FlagItemTemplate itemTemplate, IPlayer? player = null)
    {
        if (!string.IsNullOrWhiteSpace(itemTemplate.DisplayNameKey))
        {
            var key = itemTemplate.DisplayNameKey.Trim();
            var localizer = player == null ? Core.Localizer : Core.Translation.GetPlayerLocalizer(player);
            var localized = itemTemplate.Type.Equals(nameof(ShopItemType.Permanent), StringComparison.OrdinalIgnoreCase)
                ? localizer[key]
                : localizer[key, FormatDuration(itemTemplate.DurationSeconds)];
            if (!string.Equals(localized, key, StringComparison.Ordinal))
                return localized;
        }

        if (!string.IsNullOrWhiteSpace(itemTemplate.DisplayName))
            return itemTemplate.DisplayName.Trim();

        return itemTemplate.Id.Trim();
    }

    private static string FormatDuration(int totalSeconds)
    {
        if (totalSeconds <= 0)
            return "0 Seconds";

        var ts = TimeSpan.FromSeconds(totalSeconds);
        if (ts.TotalHours >= 1)
        {
            var hours = (int)ts.TotalHours;
            var minutes = ts.Minutes;
            return minutes > 0 ? $"{hours}h {minutes}m" : $"{hours}h";
        }
        if (ts.TotalMinutes >= 1)
            return $"{ts.Minutes}m";

        return $"{ts.Seconds}s";
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
            Settings = new FlagsModuleSettings { Category = DefaultCategory },
            Items = [
                new FlagItemTemplate
                {
                    Id = "flag_slot_hourly",
                    DisplayNameKey = "item.slot.name",
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
    public bool UseCorePrefix { get; set; } = true;
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