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
    Id = "Shop_Parachute",
    Name = "Shop Parachute",
    Author = "T3Marius",
    Version = "1.0.0",
    Description = "ShopCore parachute module"
)]
public class Shop_Parachute : BasePlugin
{
    private const string ShopCoreInterfaceKey = "ShopCore.API.v1";
    private const string ModulePluginId = "Shop_Parachute";
    private const string TemplateFileName = "parachute_config.jsonc";
    private const string TemplateSectionName = "Main";
    private const string DefaultCategory = "Movement/Parachute";
    private const int MaxPlayers = 65;
    private const float PhysicsUpdateIntervalSeconds = 0.05f;
    private const float VelocityEpsilon = 0.5f;

    private sealed class PlayerData
    {
        public CDynamicProp? Entity { get; set; }
        public bool Flying { get; set; }
        public bool SkipTick { get; set; } = true;
    }

    private IShopCoreApiV1? shopApi;
    private bool handlersRegistered;
    private readonly HashSet<string> registeredItemIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> registeredItemOrder = new();
    private readonly Dictionary<string, ParachuteItemRuntime> itemRuntimeById = new(StringComparer.OrdinalIgnoreCase);
    private readonly PlayerData?[] playerDataById = new PlayerData[MaxPlayers];
    private readonly Dictionary<int, float> nextPhysicsUpdateAtByPlayerId = new();

    public Shop_Parachute(ISwiftlyCore core) : base(core) { }

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
            Core.Logger.LogWarning("ShopCore API is not available. Parachute items will not be registered.");
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

        if (hotReload)
        {
            foreach (var player in Core.PlayerManager.GetAllPlayers())
            {
                if (!player.IsValid || player.IsFakeClient)
                {
                    continue;
                }

                EnsurePlayerData(player);
            }
        }

        if (shopApi is not null && !handlersRegistered)
        {
            RegisterItemsAndHandlers();
        }
    }

    public override void Unload()
    {
        Core.Event.OnTick -= OnTick;
        Core.Event.OnClientDisconnected -= OnClientDisconnected;

        foreach (var player in Core.PlayerManager.GetAllPlayers())
        {
            if (!player.IsValid || player.IsFakeClient)
            {
                continue;
            }

            ResetPlayerState(player, removeData: true);
        }

        nextPhysicsUpdateAtByPlayerId.Clear();
        UnregisterItemsAndHandlers();
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerConnectFull(EventPlayerConnectFull e)
    {
        var player = Core.PlayerManager.GetPlayer(e.UserId);
        if (player is null || !player.IsValid || player.IsFakeClient)
        {
            return HookResult.Continue;
        }

        EnsurePlayerData(player);
        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnGameEventPlayerDisconnect(EventPlayerDisconnect e)
    {
        var player = Core.PlayerManager.GetPlayer(e.UserId);
        if (player is null || !player.IsValid)
        {
            return HookResult.Continue;
        }

        ResetPlayerState(player, removeData: true);
        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Pre)]
    public HookResult OnPlayerSpawn(EventPlayerSpawn e)
    {
        var player = Core.PlayerManager.GetPlayer(e.UserId);
        if (player is null || !player.IsValid || player.IsFakeClient)
        {
            return HookResult.Continue;
        }

        ResetPlayerState(player, removeData: false);
        EnsurePlayerData(player);
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

        ResetPlayerState(player, removeData: false);
        return HookResult.Continue;
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent e)
    {
        var player = Core.PlayerManager.GetPlayer(e.PlayerId);
        if (player is null)
        {
            if (e.PlayerId >= 0 && e.PlayerId < MaxPlayers)
            {
                playerDataById[e.PlayerId] = null;
            }

            nextPhysicsUpdateAtByPlayerId.Remove(e.PlayerId);
            return;
        }

        ResetPlayerState(player, removeData: true);
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
            if (!player.IsValid || player.IsFakeClient)
            {
                continue;
            }

            var data = EnsurePlayerData(player);

            if (!TryGetEnabledParachute(player, out var runtime))
            {
                if (data.Flying)
                {
                    ResetPlayerState(player, removeData: false);
                }

                continue;
            }

            ApplyParachutePhysics(player, data, runtime, currentTime);
        }
    }

    private void RegisterItemsAndHandlers()
    {
        if (shopApi is null)
        {
            return;
        }

        UnregisterItemsAndHandlers();

        var moduleConfig = shopApi.LoadModuleConfig<ParachuteModuleConfig>(
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
            if (!TryCreateDefinition(moduleConfig.Settings, itemTemplate, category, out var definition, out var runtime))
            {
                continue;
            }

            if (!shopApi.RegisterItem(definition))
            {
                Core.Logger.LogWarning("Failed to register parachute item '{ItemId}'.", definition.Id);
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
            "Shop_Parachute initialized. RegisteredItems={RegisteredItems}",
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
            "module.parachute.error.permission",
            context.Item.DisplayName,
            runtime.RequiredPermission
        );
    }

    private void OnItemToggled(IPlayer player, ShopItemDefinition item, bool enabled)
    {
        if (!registeredItemIds.Contains(item.Id) || shopApi is null)
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

            return;
        }

        ResetPlayerState(player, removeData: false);
    }

    private void OnItemSold(IPlayer player, ShopItemDefinition item, decimal creditedAmount)
    {
        if (!registeredItemIds.Contains(item.Id))
        {
            return;
        }

        ResetPlayerState(player, removeData: false);
    }

    private void OnItemExpired(IPlayer player, ShopItemDefinition item)
    {
        if (!registeredItemIds.Contains(item.Id))
        {
            return;
        }

        ResetPlayerState(player, removeData: false);
    }

    private void ApplyParachutePhysics(IPlayer player, PlayerData data, ParachuteItemRuntime runtime, float currentTime)
    {
        var playerPawn = player.PlayerPawn;
        if (playerPawn is null || !playerPawn.IsValid || !player.IsAlive)
        {
            if (data.Flying)
            {
                ResetPlayerState(player, removeData: false);
            }

            return;
        }

        bool pressingE = (player.PressedButtons & GameButtonFlags.E) != 0;

        if (!pressingE || playerPawn.GroundEntity.IsValid)
        {
            if (data.Flying)
            {
                ResetPlayerState(player, removeData: false);
            }

            return;
        }

        if (runtime.DisableWhenCarryingHostage && IsCarryingHostage(playerPawn))
        {
            if (data.Flying)
            {
                ResetPlayerState(player, removeData: false);
            }

            return;
        }

        var velocity = playerPawn.AbsVelocity;
        if (velocity.Z >= 0.0f)
        {
            if (data.Flying)
            {
                playerPawn.ActualGravityScale = 1.0f;
                data.Flying = false;
            }

            return;
        }

        if (!string.IsNullOrWhiteSpace(runtime.Model))
        {
            data.Entity ??= CreateParachuteVisual(playerPawn, runtime.Model);
            data.SkipTick = !data.SkipTick;

            if (!data.SkipTick && data.Entity?.IsValid is true)
            {
                data.Entity.Teleport(playerPawn.AbsOrigin, playerPawn.AbsRotation, playerPawn.AbsVelocity);
            }
        }

        if (!nextPhysicsUpdateAtByPlayerId.TryGetValue(player.PlayerID, out var nextApplyAt) || currentTime >= nextApplyAt)
        {
            var newZ = ((velocity.Z >= runtime.FallSpeed && runtime.Linear) || runtime.FallDecrease == 0.0f)
                ? runtime.FallSpeed
                : velocity.Z + runtime.FallDecrease;

            if (MathF.Abs(velocity.Z - newZ) > VelocityEpsilon)
            {
                velocity.Z = newZ;
                playerPawn.AbsVelocity = velocity;
                playerPawn.VelocityUpdated();
            }

            nextPhysicsUpdateAtByPlayerId[player.PlayerID] = currentTime + PhysicsUpdateIntervalSeconds;
        }

        if (!data.Flying)
        {
            playerPawn.ActualGravityScale = runtime.GravityScale;
            data.Flying = true;
        }
    }

    private PlayerData EnsurePlayerData(IPlayer player)
    {
        var playerId = player.PlayerID;
        if (playerId < 0 || playerId >= MaxPlayers)
        {
            return new PlayerData();
        }

        var data = playerDataById[playerId];
        if (data is not null)
        {
            return data;
        }

        data = new PlayerData();
        playerDataById[playerId] = data;
        return data;
    }

    private void ResetPlayerState(IPlayer player, bool removeData)
    {
        var playerId = player.PlayerID;
        if (playerId < 0 || playerId >= MaxPlayers)
        {
            return;
        }

        if (playerDataById[playerId] is not { } data)
        {
            return;
        }

        RemoveParachute(data);
        data.Flying = false;
        data.SkipTick = true;

        var playerPawn = player.PlayerPawn;
        if (playerPawn is not null && playerPawn.IsValid)
        {
            playerPawn.ActualGravityScale = 1.0f;
        }

        nextPhysicsUpdateAtByPlayerId.Remove(playerId);

        if (removeData)
        {
            playerDataById[playerId] = null;
        }
    }

    private CDynamicProp? CreateParachuteVisual(CCSPlayerPawn playerPawn, string model)
    {
        try
        {
            var entity = Core.EntitySystem.CreateEntityByDesignerName<CDynamicProp>("prop_dynamic_override");
            if (entity?.IsValid is not true)
            {
                return null;
            }

            entity.Teleport(playerPawn.AbsOrigin, QAngle.Zero, Vector.Zero);
            entity.DispatchSpawn();
            entity.SetModel(model);
            return entity;
        }
        catch (Exception ex)
        {
            Core.Logger.LogWarning(ex, "Failed to create parachute visual entity.");
            return null;
        }
    }

    private static void RemoveParachute(PlayerData data)
    {
        if (data.Entity?.IsValid is not true)
        {
            data.Entity = null;
            return;
        }

        try
        {
            var type = data.Entity.GetType();
            var despawn = type.GetMethod("Despawn", Type.EmptyTypes);
            if (despawn is not null)
            {
                _ = despawn.Invoke(data.Entity, null);
                data.Entity = null;
                return;
            }

            var remove = type.GetMethod("Remove", Type.EmptyTypes);
            if (remove is not null)
            {
                _ = remove.Invoke(data.Entity, null);
            }
        }
        catch
        {
        }

        data.Entity = null;
    }

    private static bool IsCarryingHostage(CCSPlayerPawn playerPawn)
    {
        try
        {
            var hostageServices = playerPawn.GetType().GetProperty("HostageServices")?.GetValue(playerPawn);
            if (hostageServices is null)
            {
                return false;
            }

            var carriedHostageProp = hostageServices.GetType().GetProperty("CarriedHostageProp")?.GetValue(hostageServices);
            if (carriedHostageProp is null)
            {
                return false;
            }

            var value = carriedHostageProp.GetType().GetProperty("Value")?.GetValue(carriedHostageProp);
            return value is not null;
        }
        catch
        {
            return false;
        }
    }

    private bool TryGetEnabledParachute(IPlayer player, out ParachuteItemRuntime runtime)
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

    private bool TryCreateDefinition(
        ParachuteModuleSettings settings,
        ParachuteItemTemplate itemTemplate,
        string category,
        out ShopItemDefinition definition,
        out ParachuteItemRuntime runtime)
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
                "Skipping item '{ItemId}' because parachute items cannot use Type '{Type}'.",
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

        var fallSpeed = itemTemplate.FallSpeed ?? settings.DefaultFallSpeed;
        var fallDecrease = itemTemplate.FallDecrease ?? settings.DefaultFallDecrease;
        var linear = itemTemplate.Linear ?? settings.DefaultLinear;
        var gravityScale = itemTemplate.GravityScale ?? settings.DefaultGravityScale;
        var disableWhenCarryingHostage = itemTemplate.DisableWhenCarryingHostage ?? settings.DisableWhenCarryingHostage;
        var model = string.IsNullOrWhiteSpace(itemTemplate.Model)
            ? settings.DefaultModel
            : itemTemplate.Model.Trim();

        runtime = new ParachuteItemRuntime(
            ItemId: itemId,
            FallSpeed: -MathF.Abs(Math.Max(1f, fallSpeed)),
            FallDecrease: MathF.Abs(Math.Max(0f, fallDecrease)),
            Linear: linear,
            GravityScale: Math.Clamp(gravityScale, 0.01f, 1.0f),
            Model: model,
            DisableWhenCarryingHostage: disableWhenCarryingHostage,
            RequiredPermission: itemTemplate.RequiredPermission?.Trim() ?? string.Empty
        );

        return true;
    }

    private string ResolveDisplayName(ParachuteItemTemplate itemTemplate)
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

    private static void NormalizeConfig(ParachuteModuleConfig config)
    {
        config.Settings ??= new ParachuteModuleSettings();
        config.Items ??= [];

        if (config.Settings.DefaultFallSpeed <= 0)
        {
            config.Settings.DefaultFallSpeed = 85f;
        }

        if (config.Settings.DefaultFallDecrease < 0)
        {
            config.Settings.DefaultFallDecrease = 15f;
        }

        if (config.Settings.DefaultGravityScale <= 0f)
        {
            config.Settings.DefaultGravityScale = 0.1f;
        }
    }

    private static ParachuteModuleConfig CreateDefaultConfig()
    {
        return new ParachuteModuleConfig
        {
            Settings = new ParachuteModuleSettings
            {
                Category = DefaultCategory,
                DefaultFallSpeed = 85f,
                DefaultFallDecrease = 15f,
                DefaultLinear = true,
                DefaultGravityScale = 0.1f,
                DefaultModel = "",
                DisableWhenCarryingHostage = false
            },
            Items =
            [
                new ParachuteItemTemplate
                {
                    Id = "parachute_basic_hourly",
                    DisplayNameKey = "module.parachute.item.basic.name",
                    Price = 500,
                    SellPrice = 250,
                    DurationSeconds = 3600,
                    FallSpeed = 85f,
                    FallDecrease = 15f,
                    Linear = true,
                    GravityScale = 0.1f,
                    Type = nameof(ShopItemType.Temporary),
                    Team = nameof(ShopItemTeam.Any),
                    Enabled = true,
                    CanBeSold = true
                },
                new ParachuteItemTemplate
                {
                    Id = "parachute_premium_weekly",
                    DisplayNameKey = "module.parachute.item.premium.name",
                    Price = 1500,
                    SellPrice = 750,
                    DurationSeconds = 604800,
                    FallSpeed = 50f,
                    FallDecrease = 10f,
                    Linear = true,
                    GravityScale = 0.1f,
                    Type = nameof(ShopItemType.Temporary),
                    Team = nameof(ShopItemTeam.Any),
                    Enabled = true,
                    CanBeSold = true
                },
                new ParachuteItemTemplate
                {
                    Id = "parachute_permanent",
                    DisplayNameKey = "module.parachute.item.permanent.name",
                    Price = 8000,
                    SellPrice = 4000,
                    DurationSeconds = 0,
                    FallSpeed = 40f,
                    FallDecrease = 5f,
                    Linear = true,
                    GravityScale = 0.1f,
                    Type = nameof(ShopItemType.Permanent),
                    Team = nameof(ShopItemTeam.Any),
                    Enabled = true,
                    CanBeSold = true
                }
            ]
        };
    }
}

internal readonly record struct ParachuteItemRuntime(
    string ItemId,
    float FallSpeed,
    float FallDecrease,
    bool Linear,
    float GravityScale,
    string Model,
    bool DisableWhenCarryingHostage,
    string RequiredPermission
);

internal sealed class ParachuteModuleConfig
{
    public ParachuteModuleSettings Settings { get; set; } = new();
    public List<ParachuteItemTemplate> Items { get; set; } = [];
}

internal sealed class ParachuteModuleSettings
{
    public string Category { get; set; } = "Movement/Parachute";
    public float DefaultFallSpeed { get; set; } = 85f;
    public bool DefaultLinear { get; set; } = true;
    public float DefaultFallDecrease { get; set; } = 15f;
    public float DefaultGravityScale { get; set; } = 0.1f;
    public string DefaultModel { get; set; } = string.Empty;
    public bool DisableWhenCarryingHostage { get; set; } = false;
}

internal sealed class ParachuteItemTemplate
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
    public float? FallSpeed { get; set; }
    public float? FallDecrease { get; set; }
    public bool? Linear { get; set; }
    public float? GravityScale { get; set; }
    public string Model { get; set; } = string.Empty;
    public bool? DisableWhenCarryingHostage { get; set; }
    public string RequiredPermission { get; set; } = string.Empty;
}

