using Microsoft.Extensions.Logging;
using ShopCore.Contract;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameEvents;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Plugins;
using SwiftlyS2.Shared.SchemaDefinitions;

namespace ShopCore;

[PluginMetadata(
    Id = "Shop_PlayerColor",
    Name = "Shop PlayerColor",
    Author = "T3Marius",
    Version = "1.0.0",
    Description = "ShopCore module with player color items"
)]
public class Shop_PlayerColor : BasePlugin
{
    private const string ShopCoreInterfaceKey = "ShopCore.API.v1";
    private const string ModulePluginId = "Shop_PlayerColor";
    private const string TemplateFileName = "playercolor_config.jsonc";
    private const string TemplateSectionName = "Main";
    private const string DefaultCategory = "Visuals/Player Colors";

    private static readonly Color DefaultPlayerColor = new(255, 255, 255, 255);

    private readonly HashSet<string> registeredItemIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> registeredItemOrder = new();
    private readonly Dictionary<string, PlayerColorItemRuntime> itemRuntimeById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, float> nextRainbowUpdateAtByPlayerId = new();
    private readonly Random random = new();

    private IShopCoreApiV1? shopApi;
    private bool handlersRegistered;

    public Shop_PlayerColor(ISwiftlyCore core) : base(core)
    {
    }

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
            Core.Logger.LogWarning("ShopCore API is not available. PlayerColor items will not be registered.");
            return;
        }

        if (!handlersRegistered)
        {
            RegisterItemsAndHandlers();
        }
    }

    public override void Load(bool hotReload)
    {
        Core.Event.OnTick += OnTick;
        Core.Event.OnClientDisconnected += OnClientDisconnected;

        if (shopApi is not null && !handlersRegistered)
        {
            RegisterItemsAndHandlers();
        }

        if (hotReload)
        {
            foreach (var player in Core.PlayerManager.GetAllValidPlayers())
            {
                RefreshPlayerColor(player);
            }
        }
    }

    public override void Unload()
    {
        Core.Event.OnTick -= OnTick;
        Core.Event.OnClientDisconnected -= OnClientDisconnected;

        foreach (var player in Core.PlayerManager.GetAllValidPlayers())
        {
            ResetPlayerColor(player);
        }

        nextRainbowUpdateAtByPlayerId.Clear();
        UnregisterItemsAndHandlers();
    }

    [GameEventHandler(HookMode.Pre)]
    public HookResult OnPlayerSpawn(EventPlayerSpawn e)
    {
        var player = Core.PlayerManager.GetPlayer(e.UserId);
        if (player is null || !player.IsValid || player.IsFakeClient)
        {
            return HookResult.Continue;
        }

        RefreshPlayerColor(player);
        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Pre)]
    public HookResult OnPlayerDeath(EventPlayerDeath e)
    {
        var player = Core.PlayerManager.GetPlayer(e.UserId);
        if (player is null || !player.IsValid || player.IsFakeClient)
        {
            return HookResult.Continue;
        }

        nextRainbowUpdateAtByPlayerId.Remove(player.PlayerID);
        return HookResult.Continue;
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent e)
    {
        nextRainbowUpdateAtByPlayerId.Remove(e.PlayerId);
    }

    private void OnTick()
    {
        if (shopApi is null || !handlersRegistered || registeredItemOrder.Count == 0)
        {
            return;
        }

        var currentTime = Core.Engine.GlobalVars.CurrentTime;

        foreach (var player in Core.PlayerManager.GetAllValidPlayers())
        {
            if (player.IsFakeClient)
            {
                continue;
            }

            if (!TryGetEnabledRuntime(player, out var runtime))
            {
                nextRainbowUpdateAtByPlayerId.Remove(player.PlayerID);
                continue;
            }

            if (!runtime.IsRainbow)
            {
                nextRainbowUpdateAtByPlayerId.Remove(player.PlayerID);
                continue;
            }

            if (nextRainbowUpdateAtByPlayerId.TryGetValue(player.PlayerID, out var nextUpdateAt) && currentTime < nextUpdateAt)
            {
                continue;
            }

            var rainbowColor = NextRainbowColor();
            ApplyColor(player, rainbowColor);
            nextRainbowUpdateAtByPlayerId[player.PlayerID] = currentTime + runtime.RainbowUpdateIntervalSeconds;
        }
    }

    private void RegisterItemsAndHandlers()
    {
        if (shopApi is null)
        {
            return;
        }

        UnregisterItemsAndHandlers();

        var moduleConfig = shopApi.LoadModuleConfig<PlayerColorModuleConfig>(
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
            if (!TryCreateDefinition(itemTemplate, moduleConfig.Settings, category, out var definition, out var runtime))
            {
                continue;
            }

            if (!shopApi.RegisterItem(definition))
            {
                Core.Logger.LogWarning("Failed to register player color item '{ItemId}'.", definition.Id);
                continue;
            }

            _ = registeredItemIds.Add(definition.Id);
            registeredItemOrder.Add(definition.Id);
            itemRuntimeById[definition.Id] = runtime;
            registeredCount++;
        }

        shopApi.OnBeforeItemPurchase += OnBeforeItemPurchase;
        shopApi.OnItemToggled += OnItemToggled;
        shopApi.OnItemSold += OnItemSold;
        shopApi.OnItemExpired += OnItemExpired;
        handlersRegistered = true;

        Core.Logger.LogInformation(
            "Shop_PlayerColor initialized. RegisteredItems={RegisteredItems}",
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
        shopApi.OnItemToggled -= OnItemToggled;
        shopApi.OnItemSold -= OnItemSold;
        shopApi.OnItemExpired -= OnItemExpired;

        foreach (var itemId in registeredItemIds)
        {
            _ = shopApi.UnregisterItem(itemId);
        }

        registeredItemIds.Clear();
        registeredItemOrder.Clear();
        itemRuntimeById.Clear();
        nextRainbowUpdateAtByPlayerId.Clear();
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
            "module.player_color.error.permission",
            context.Item.DisplayName,
            runtime.RequiredPermission
        );
    }

    private void OnItemToggled(IPlayer player, ShopItemDefinition item, bool enabled)
    {
        if (shopApi is null || !registeredItemIds.Contains(item.Id))
        {
            return;
        }

        if (enabled)
        {
            foreach (var otherItemId in registeredItemOrder)
            {
                if (string.Equals(otherItemId, item.Id, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!shopApi.IsItemEnabled(player, otherItemId))
                {
                    continue;
                }

                _ = shopApi.SetItemEnabled(player, otherItemId, false);
            }
        }

        RefreshPlayerColor(player);
    }

    private void OnItemSold(IPlayer player, ShopItemDefinition item, decimal amount)
    {
        if (!registeredItemIds.Contains(item.Id))
        {
            return;
        }

        RefreshPlayerColor(player);
    }

    private void OnItemExpired(IPlayer player, ShopItemDefinition item)
    {
        if (!registeredItemIds.Contains(item.Id))
        {
            return;
        }

        RefreshPlayerColor(player);
    }

    private void RefreshPlayerColor(IPlayer player)
    {
        if (shopApi is null || player is null || !player.IsValid || player.IsFakeClient)
        {
            return;
        }

        if (!TryGetEnabledRuntime(player, out var runtime))
        {
            nextRainbowUpdateAtByPlayerId.Remove(player.PlayerID);
            ResetPlayerColor(player);
            return;
        }

        if (runtime.IsRainbow)
        {
            var color = NextRainbowColor();
            ApplyColor(player, color);
            nextRainbowUpdateAtByPlayerId[player.PlayerID] = Core.Engine.GlobalVars.CurrentTime + runtime.RainbowUpdateIntervalSeconds;
            return;
        }

        nextRainbowUpdateAtByPlayerId.Remove(player.PlayerID);
        ApplyColor(player, runtime.StaticColor);
    }

    private bool TryGetEnabledRuntime(IPlayer player, out PlayerColorItemRuntime runtime)
    {
        runtime = default;

        if (shopApi is null)
        {
            return false;
        }

        foreach (var itemId in registeredItemOrder)
        {
            if (!itemRuntimeById.TryGetValue(itemId, out var itemRuntime))
            {
                continue;
            }

            if (!shopApi.IsItemEnabled(player, itemId))
            {
                continue;
            }

            runtime = itemRuntime;
            return true;
        }

        return false;
    }

    private void ApplyColor(IPlayer player, Color color)
    {
        Core.Scheduler.NextWorldUpdate(() =>
        {
            if (!TryGetAlivePawn(player, out var pawn))
            {
                return;
            }

            pawn.Render = color;
            pawn.RenderUpdated();
        });
    }

    private void ResetPlayerColor(IPlayer player)
    {
        Core.Scheduler.NextWorldUpdate(() =>
        {
            if (!TryGetAlivePawn(player, out var pawn))
            {
                return;
            }

            pawn.Render = DefaultPlayerColor;
            pawn.RenderUpdated();
        });
    }

    private bool TryGetAlivePawn(IPlayer player, out CCSPlayerPawn pawn)
    {
        pawn = null!;

        if (player is null || !player.IsValid)
        {
            return false;
        }

        var playerPawn = player.PlayerPawn;
        if (playerPawn is null || !playerPawn.IsValid)
        {
            return false;
        }

        pawn = playerPawn;
        return pawn.LifeState == (int)LifeState_t.LIFE_ALIVE;
    }

    private Color NextRainbowColor()
    {
        lock (random)
        {
            return new Color(
                random.Next(0, 256),
                random.Next(0, 256),
                random.Next(0, 256),
                255
            );
        }
    }

    private bool TryCreateDefinition(
        PlayerColorItemTemplate itemTemplate,
        PlayerColorModuleSettings settings,
        string category,
        out ShopItemDefinition definition,
        out PlayerColorItemRuntime runtime)
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

        if (!Enum.TryParse(itemTemplate.Type, ignoreCase: true, out ShopItemType itemType))
        {
            Core.Logger.LogWarning("Skipping item '{ItemId}' because Type '{Type}' is invalid.", itemId, itemTemplate.Type);
            return false;
        }

        if (itemType == ShopItemType.Consumable)
        {
            Core.Logger.LogWarning(
                "Skipping item '{ItemId}' because player color items cannot use Type '{Type}'.",
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

        var isRainbow = string.Equals(itemTemplate.Color?.Trim(), "rainbow", StringComparison.OrdinalIgnoreCase);
        var staticColor = DefaultPlayerColor;

        if (!isRainbow)
        {
            if (!TryResolveColor(itemTemplate.Color, out staticColor))
            {
                Core.Logger.LogWarning(
                    "Skipping item '{ItemId}' because Color '{Color}' is invalid.",
                    itemId,
                    itemTemplate.Color
                );
                return false;
            }
        }

        var rainbowInterval = itemTemplate.RainbowUpdateIntervalSeconds ?? settings.DefaultRainbowUpdateIntervalSeconds;
        if (rainbowInterval < 0.05f)
        {
            rainbowInterval = 0.05f;
        }

        definition = new ShopItemDefinition(
            Id: itemId,
            DisplayName: ResolveDisplayName(itemTemplate, isRainbow),
            Category: category,
            Price: itemTemplate.Price,
            SellPrice: sellPrice,
            Duration: duration,
            Type: itemType,
            Team: team,
            Enabled: itemTemplate.Enabled,
            CanBeSold: itemTemplate.CanBeSold
        );

        runtime = new PlayerColorItemRuntime(
            ItemId: itemId,
            IsRainbow: isRainbow,
            StaticColor: staticColor,
            RainbowUpdateIntervalSeconds: rainbowInterval,
            RequiredPermission: itemTemplate.RequiredPermission?.Trim() ?? string.Empty
        );

        return true;
    }

    private string ResolveDisplayName(PlayerColorItemTemplate itemTemplate, bool isRainbow)
    {
        if (!string.IsNullOrWhiteSpace(itemTemplate.DisplayNameKey))
        {
            var key = itemTemplate.DisplayNameKey.Trim();
            string localized;

            if (itemTemplate.Type.Equals(nameof(ShopItemType.Permanent), StringComparison.OrdinalIgnoreCase))
            {
                localized = Core.Localizer[key, itemTemplate.Color];
            }
            else
            {
                var duration = FormatDuration(itemTemplate.DurationSeconds);
                localized = isRainbow
                    ? Core.Localizer[key, duration]
                    : Core.Localizer[key, itemTemplate.Color, duration];
            }

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

    private static bool TryResolveColor(string? value, out Color color)
    {
        color = DefaultPlayerColor;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();

        if (text.StartsWith('#'))
        {
            try
            {
                color = Color.FromHex(text);
                return true;
            }
            catch
            {
                return false;
            }
        }

        var builtin = System.Drawing.Color.FromName(text);
        if (!builtin.IsKnownColor && !builtin.IsNamedColor && !builtin.IsSystemColor)
        {
            return false;
        }

        color = Color.FromBuiltin(builtin);
        return true;
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

    private static void NormalizeConfig(PlayerColorModuleConfig config)
    {
        config.Settings ??= new PlayerColorModuleSettings();
        config.Items ??= [];

        if (config.Settings.DefaultRainbowUpdateIntervalSeconds < 0.05f)
        {
            config.Settings.DefaultRainbowUpdateIntervalSeconds = 0.5f;
        }
    }

    private static PlayerColorModuleConfig CreateDefaultConfig()
    {
        return new PlayerColorModuleConfig
        {
            Settings = new PlayerColorModuleSettings
            {
                Category = DefaultCategory,
                DefaultRainbowUpdateIntervalSeconds = 0.5f
            },
            Items =
            [
                new PlayerColorItemTemplate
                {
                    Id = "player_color_red_hourly",
                    DisplayNameKey = "module.player_color.item.color.name",
                    Color = "Red",
                    Price = 700,
                    SellPrice = 350,
                    DurationSeconds = 3600,
                    Type = nameof(ShopItemType.Temporary),
                    Team = nameof(ShopItemTeam.Any),
                    Enabled = true,
                    CanBeSold = true
                },
                new PlayerColorItemTemplate
                {
                    Id = "player_color_blue_hourly",
                    DisplayNameKey = "module.player_color.item.color.name",
                    Color = "Blue",
                    Price = 700,
                    SellPrice = 350,
                    DurationSeconds = 3600,
                    Type = nameof(ShopItemType.Temporary),
                    Team = nameof(ShopItemTeam.Any),
                    Enabled = true,
                    CanBeSold = true
                },
                new PlayerColorItemTemplate
                {
                    Id = "player_color_green_hourly",
                    DisplayNameKey = "module.player_color.item.color.name",
                    Color = "Green",
                    Price = 700,
                    SellPrice = 350,
                    DurationSeconds = 3600,
                    Type = nameof(ShopItemType.Temporary),
                    Team = nameof(ShopItemTeam.Any),
                    Enabled = true,
                    CanBeSold = true
                },
                new PlayerColorItemTemplate
                {
                    Id = "player_color_rainbow_hourly",
                    DisplayNameKey = "module.player_color.item.rainbow.name",
                    Color = "Rainbow",
                    Price = 1500,
                    SellPrice = 750,
                    DurationSeconds = 3600,
                    Type = nameof(ShopItemType.Temporary),
                    Team = nameof(ShopItemTeam.Any),
                    Enabled = true,
                    CanBeSold = true
                },
                new PlayerColorItemTemplate
                {
                    Id = "player_color_purple_permanent",
                    DisplayNameKey = "module.player_color.item.permanent.name",
                    Color = "Purple",
                    Price = 8000,
                    SellPrice = 4000,
                    DurationSeconds = 0,
                    Type = nameof(ShopItemType.Permanent),
                    Team = nameof(ShopItemTeam.Any),
                    Enabled = true,
                    CanBeSold = true
                }
            ]
        };
    }
}

internal readonly record struct PlayerColorItemRuntime(
    string ItemId,
    bool IsRainbow,
    Color StaticColor,
    float RainbowUpdateIntervalSeconds,
    string RequiredPermission
);

internal sealed class PlayerColorModuleConfig
{
    public PlayerColorModuleSettings Settings { get; set; } = new();
    public List<PlayerColorItemTemplate> Items { get; set; } = [];
}

internal sealed class PlayerColorModuleSettings
{
    public string Category { get; set; } = "Visuals/Player Colors";
    public float DefaultRainbowUpdateIntervalSeconds { get; set; } = 0.5f;
}

internal sealed class PlayerColorItemTemplate
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string DisplayNameKey { get; set; } = string.Empty;
    public int Price { get; set; } = 0;
    public int? SellPrice { get; set; }
    public int DurationSeconds { get; set; } = 0;
    public string Type { get; set; } = nameof(ShopItemType.Temporary);
    public string Team { get; set; } = nameof(ShopItemTeam.Any);
    public bool Enabled { get; set; } = true;
    public bool CanBeSold { get; set; } = true;
    public string Color { get; set; } = "White";
    public float? RainbowUpdateIntervalSeconds { get; set; }
    public string RequiredPermission { get; set; } = string.Empty;
}

