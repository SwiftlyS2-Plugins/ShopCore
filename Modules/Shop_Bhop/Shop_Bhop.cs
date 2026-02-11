using Microsoft.Extensions.Logging;
using ShopCore.Contract;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Convars;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Plugins;

namespace ShopCore;

[PluginMetadata(
    Id = "Shop_Bhop",
    Name = "Shop Bhop",
    Author = "T3Marius",
    Version = "1.0.0",
    Description = "ShopCore module with bhop items."
)]
public class Shop_Bhop : BasePlugin
{
    private const string ShopCoreInterfaceKey = "ShopCore.API.v1";
    private const string ModulePluginId = "Shop_Bhop";
    private const string TemplateFileName = "bhop_config.jsonc";
    private const string TemplateSectionName = "Main";
    private const string DefaultCategory = "Movement/Bhop";

    private IShopCoreApiV1? shopApi;
    private bool handlersRegistered;

    private IConVar<bool>? autoBhopConVar;
    private IConVar<bool>? enableBhopConVar;
    private ConvarFlags? autoBhopOriginalFlags;
    private ConvarFlags? enableBhopOriginalFlags;

    private readonly HashSet<string> registeredItemIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> registeredItemOrder = new();
    private readonly Dictionary<string, BhopItemRuntime> itemRuntimeById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, BhopPlayerState> playerStateById = new();
    private int pendingConVarSync;

    public Shop_Bhop(ISwiftlyCore core) : base(core) { }

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
            Core.Logger.LogWarning("ShopCore API is not available. Bhop items will not be registered.");
            return;
        }

        if (!handlersRegistered)
        {
            RegisterItemsAndHandlers();
        }
    }

    public override void Load(bool hotReload)
    {
        autoBhopConVar = Core.ConVar.Find<bool>("sv_autobunnyhopping");
        enableBhopConVar = Core.ConVar.Find<bool>("sv_enablebunnyhopping");

        if (autoBhopConVar is not null)
        {
            autoBhopOriginalFlags = autoBhopConVar.Flags;
        }
        if (enableBhopConVar is not null)
        {
            enableBhopOriginalFlags = enableBhopConVar.Flags;
        }

        Core.Event.OnClientConnected += OnClientConnected;
        Core.Event.OnClientDisconnected += OnClientDisconnected;
        Core.Event.OnTick += OnTick;

        if (shopApi is not null && !handlersRegistered)
        {
            RegisterItemsAndHandlers();
        }
    }

    public override void Unload()
    {
        Core.Event.OnClientConnected -= OnClientConnected;
        Core.Event.OnClientDisconnected -= OnClientDisconnected;
        Core.Event.OnTick -= OnTick;

        RunOnMainThread(DisableBhopGlobally);
        UnregisterItemsAndHandlers();
    }

    private void RegisterItemsAndHandlers()
    {
        if (shopApi is null)
        {
            return;
        }

        UnregisterItemsAndHandlers();

        var moduleConfig = shopApi.LoadModuleConfig<BhopModuleConfig>(
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
                Core.Logger.LogWarning("Failed to register bhop item '{ItemId}'.", definition.Id);
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

        Core.Logger.LogInformation("Shop_Bhop initialized. RegisteredItems={RegisteredItems}", registeredCount);
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
        playerStateById.Clear();
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
            "module.bhop.error.permission",
            context.Item.DisplayName,
            runtime.RequiredPermission
        );
    }

    private void OnItemToggled(IPlayer player, ShopItemDefinition item, bool enabled)
    {
        if (!registeredItemIds.Contains(item.Id) || shopApi is null || !player.IsValid)
        {
            return;
        }

        // Keep only one bhop item enabled at a time for a player.
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

        RunOnMainThread(() => RefreshPlayerBhopState(player));
    }

    private void OnItemSold(IPlayer player, ShopItemDefinition item, decimal creditedAmount)
    {
        if (!registeredItemIds.Contains(item.Id))
        {
            return;
        }

        RunOnMainThread(() => RefreshPlayerBhopState(player));
    }

    private void OnItemExpired(IPlayer player, ShopItemDefinition item)
    {
        if (!registeredItemIds.Contains(item.Id))
        {
            return;
        }

        RunOnMainThread(() => RefreshPlayerBhopState(player));
    }

    private void OnClientConnected(IOnClientConnectedEvent @event)
    {
        RunOnMainThread(() =>
        {
            playerStateById[@event.PlayerId] = new BhopPlayerState();
            UpdateGlobalBhopConVarState();
        });
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        RunOnMainThread(() =>
        {
            if (playerStateById.Remove(@event.PlayerId, out var removed) && removed.Active)
            {
                UpdateGlobalBhopConVarState();
            }
        });
    }

    private void OnTick()
    {
        if (shopApi is null || registeredItemOrder.Count == 0)
        {
            return;
        }

        foreach (var player in Core.PlayerManager.GetAllValidPlayers())
        {
            if (!player.IsValid || player.IsFakeClient || !player.IsAlive)
            {
                DeactivatePlayerBhop(player);
                continue;
            }

            if (!TryGetActiveBhop(player, out var runtime))
            {
                DeactivatePlayerBhop(player);
                continue;
            }

            ActivatePlayerBhop(player, runtime);
            ClampPlayerSpeed(player, runtime.MaxSpeed);
        }
    }

    private bool TryGetActiveBhop(IPlayer player, out BhopItemRuntime runtime)
    {
        runtime = default;
        if (shopApi is null)
        {
            return false;
        }

        foreach (var itemId in registeredItemOrder)
        {
            if (!shopApi.IsItemEnabled(player, itemId))
            {
                continue;
            }

            if (!itemRuntimeById.TryGetValue(itemId, out runtime))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private void ActivatePlayerBhop(IPlayer player, BhopItemRuntime runtime)
    {
        if (!playerStateById.TryGetValue(player.PlayerID, out var state))
        {
            state = new BhopPlayerState();
            playerStateById[player.PlayerID] = state;
        }

        var changed = !state.Active ||
                      state.MaxSpeed != runtime.MaxSpeed ||
                      !string.Equals(state.ItemId, runtime.ItemId, StringComparison.OrdinalIgnoreCase);

        if (!changed)
        {
            return;
        }

        state.Active = true;
        state.MaxSpeed = runtime.MaxSpeed;
        state.ItemId = runtime.ItemId;
        playerStateById[player.PlayerID] = state;

        SetClientBhop(player, true);
        UpdateGlobalBhopConVarState();
    }

    private void DeactivatePlayerBhop(IPlayer player)
    {
        if (!playerStateById.TryGetValue(player.PlayerID, out var state) || !state.Active)
        {
            return;
        }

        state.Active = false;
        state.MaxSpeed = 0;
        state.ItemId = string.Empty;
        playerStateById[player.PlayerID] = state;

        if (player.IsValid && !player.IsFakeClient)
        {
            SetClientBhop(player, false);
        }

        UpdateGlobalBhopConVarState();
    }

    private void RefreshPlayerBhopState(IPlayer player)
    {
        if (!player.IsValid || player.IsFakeClient)
        {
            return;
        }

        if (TryGetActiveBhop(player, out var runtime))
        {
            ActivatePlayerBhop(player, runtime);
            return;
        }

        DeactivatePlayerBhop(player);
    }

    private void SetClientBhop(IPlayer player, bool value)
    {
        if (!player.IsValid || player.PlayerID < 0)
        {
            return;
        }

        RunOnMainThread(() =>
        {
            if (!player.IsValid || player.PlayerID < 0)
            {
                return;
            }

            enableBhopConVar?.ReplicateToClient(player.PlayerID, value);
            autoBhopConVar?.ReplicateToClient(player.PlayerID, value);
        });
    }

    private void UpdateGlobalBhopConVarState()
    {
        // Called from different callbacks; always apply convar writes on main thread.
        QueueConVarSync();
    }

    private void QueueConVarSync()
    {
        if (Interlocked.Exchange(ref pendingConVarSync, 1) == 1)
        {
            return;
        }

        RunOnMainThread(() =>
        {
            Interlocked.Exchange(ref pendingConVarSync, 0);
            ApplyGlobalBhopConVarState();
        });
    }

    private void ApplyGlobalBhopConVarState()
    {
        if (enableBhopConVar is null || autoBhopConVar is null)
        {
            return;
        }

        var hasActivePlayers = playerStateById.Values.Any(static state => state.Active);
        if (hasActivePlayers)
        {
            if ((enableBhopConVar.Flags & ConvarFlags.CHEAT) != 0)
            {
                enableBhopConVar.Flags &= ~ConvarFlags.CHEAT;
            }
            if ((autoBhopConVar.Flags & ConvarFlags.CHEAT) != 0)
            {
                autoBhopConVar.Flags &= ~ConvarFlags.CHEAT;
            }

            if (!enableBhopConVar.Value)
            {
                enableBhopConVar.SetInternal(true);
            }
            if (!autoBhopConVar.Value)
            {
                autoBhopConVar.SetInternal(true);
            }
            return;
        }

        DisableBhopGlobally();
    }

    private void DisableBhopGlobally()
    {
        if (enableBhopConVar is not null)
        {
            if (enableBhopConVar.Value)
            {
                enableBhopConVar.SetInternal(false);
            }
            if (enableBhopOriginalFlags.HasValue)
            {
                enableBhopConVar.Flags = enableBhopOriginalFlags.Value;
            }
        }

        if (autoBhopConVar is not null)
        {
            if (autoBhopConVar.Value)
            {
                autoBhopConVar.SetInternal(false);
            }
            if (autoBhopOriginalFlags.HasValue)
            {
                autoBhopConVar.Flags = autoBhopOriginalFlags.Value;
            }
        }
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
                Core.Logger.LogWarning(ex, "Shop_Bhop main-thread action failed.");
            }
        });
    }

    private static void ClampPlayerSpeed(IPlayer player, int maxSpeed)
    {
        if (maxSpeed <= 0)
        {
            return;
        }

        var pawn = player.PlayerPawn;
        if (pawn is null || !pawn.IsValid)
        {
            return;
        }

        var velocity = pawn.AbsVelocity;
        var horizontalSpeed = Math.Sqrt((velocity.X * velocity.X) + (velocity.Y * velocity.Y));
        if (horizontalSpeed <= maxSpeed || horizontalSpeed <= 0.001f)
        {
            return;
        }

        var ratio = (float)(maxSpeed / horizontalSpeed);
        pawn.AbsVelocity.X *= ratio;
        pawn.AbsVelocity.Y *= ratio;
        pawn.VelocityUpdated();
    }

    private bool TryCreateDefinition(
        BhopItemTemplate itemTemplate,
        string category,
        out ShopItemDefinition definition,
        out BhopItemRuntime runtime)
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
            Core.Logger.LogWarning("Skipping item '{ItemId}' because bhop items cannot use Type '{Type}'.", itemId, itemType);
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
            Core.Logger.LogWarning("Skipping item '{ItemId}' because Temporary items require DurationSeconds > 0.", itemId);
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

        runtime = new BhopItemRuntime(
            ItemId: itemId,
            MaxSpeed: Math.Max(0, itemTemplate.MaxSpeed),
            RequiredPermission: itemTemplate.RequiredPermission?.Trim() ?? string.Empty
        );

        return true;
    }

    private string ResolveDisplayName(BhopItemTemplate itemTemplate)
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

    private static void NormalizeConfig(BhopModuleConfig config)
    {
        config.Settings ??= new BhopModuleSettings();
        config.Items ??= [];
    }

    private static BhopModuleConfig CreateDefaultConfig()
    {
        return new BhopModuleConfig
        {
            Settings = new BhopModuleSettings
            {
                Category = DefaultCategory
            },
            Items =
            [
                new BhopItemTemplate
                {
                    Id = "bhop_fullspeed_hourly",
                    DisplayNameKey = "module.bhop.item.fullspeed.name",
                    Price = 2500,
                    SellPrice = 1250,
                    DurationSeconds = 3600,
                    MaxSpeed = 0,
                    Type = nameof(ShopItemType.Temporary),
                    Team = nameof(ShopItemTeam.Any),
                    Enabled = true,
                    CanBeSold = true
                },
                new BhopItemTemplate
                {
                    Id = "bhop_limited_350_hourly",
                    DisplayNameKey = "module.bhop.item.limited350.name",
                    Price = 2000,
                    SellPrice = 1000,
                    DurationSeconds = 3600,
                    MaxSpeed = 350,
                    Type = nameof(ShopItemType.Temporary),
                    Team = nameof(ShopItemTeam.Any),
                    Enabled = true,
                    CanBeSold = true
                }
            ]
        };
    }
}

internal readonly record struct BhopItemRuntime(string ItemId, int MaxSpeed, string RequiredPermission);

internal sealed class BhopPlayerState
{
    public bool Active { get; set; }
    public int MaxSpeed { get; set; }
    public string ItemId { get; set; } = string.Empty;
}

internal sealed class BhopModuleConfig
{
    public BhopModuleSettings Settings { get; set; } = new();
    public List<BhopItemTemplate> Items { get; set; } = [];
}

internal sealed class BhopModuleSettings
{
    public string Category { get; set; } = "Movement/Bhop";
}

internal sealed class BhopItemTemplate
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string DisplayNameKey { get; set; } = string.Empty;
    public int Price { get; set; } = 0;
    public int? SellPrice { get; set; }
    public int DurationSeconds { get; set; } = 0;
    public int MaxSpeed { get; set; } = 0;
    public string Type { get; set; } = nameof(ShopItemType.Temporary);
    public string Team { get; set; } = nameof(ShopItemTeam.Any);
    public bool Enabled { get; set; } = true;
    public bool CanBeSold { get; set; } = true;
    public string RequiredPermission { get; set; } = string.Empty;
}

