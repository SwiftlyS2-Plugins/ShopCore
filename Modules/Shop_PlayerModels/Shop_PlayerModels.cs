using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    Id = "Shop_PlayerModels",
    Name = "Shop PlayerModels",
    Author = "T3Marius",
    Version = "1.0.1",
    Description = "ShopCore module with player model items"
)]
public class Shop_PlayerModels : BasePlugin
{
    private const string ShopCoreInterfaceKey = "ShopCore.API.v2";
    private const string ConfigFileName = "config.jsonc";
    private const string ConfigSectionName = "Main";
    private const string DefaultCategory = "Visuals/Player Models";
    private const float PreviewDurationSeconds = 8f;
    private const float PreviewDistance = 75f;
    private const float PreviewRotateIntervalSeconds = 0.05f;

    private IShopCoreApiV2? shopApi;
    private bool handlersRegistered;

    private readonly HashSet<string> registeredItemIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> registeredItemOrder = new();
    private readonly Dictionary<string, PlayerModelItemRuntime> itemRuntimeById = new(StringComparer.OrdinalIgnoreCase);
    private readonly object previewSync = new();
    private readonly Dictionary<int, PlayerModelPreviewState> previewStateByPlayerId = new();
    private long previewSessionCounter;

    private PlayerModelsModuleConfig? moduleConfig;
    private ServiceProvider? serviceProvider;
    private PlayerModelsModuleSettings runtimeSettings = new();

    public Shop_PlayerModels(ISwiftlyCore core) : base(core)
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
        {
            Core.Logger.LogWarning("ShopCore API is not available. PlayerModels items will not be registered.");
            return;
        }

        RegisterItemsAndHandlers();
    }

    public override void Load(bool hotReload)
    {
        LoadModuleConfig();
        Core.Event.OnPrecacheResource += OnPrecacheResource;

        if (shopApi is not null && !handlersRegistered)
        {
            RegisterItemsAndHandlers();
        }

        if (hotReload)
        {
            foreach (var player in Core.PlayerManager.GetAllValidPlayers())
            {
                ApplyConfiguredOrDefaultModel(player);
            }
        }
    }

    public override void Unload()
    {
        Core.Event.OnPrecacheResource -= OnPrecacheResource;

        foreach (var player in Core.PlayerManager.GetAllValidPlayers())
        {
            ResetToTeamDefaultModel(player);
        }

        UnregisterItemsAndHandlers();
        serviceProvider?.Dispose();
        serviceProvider = null;
        moduleConfig = null;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerSpawn(EventPlayerSpawn e)
    {
        var player = Core.PlayerManager.GetPlayer(e.UserId);
        if (player == null || !player.IsValid || player.IsFakeClient)
        {
            return HookResult.Continue;
        }

        Core.Scheduler.NextWorldUpdate(() =>
        {
            if (player == null || !player.IsValid || player.IsFakeClient)
            {
                return;
            }

            ApplyConfiguredOrDefaultModel(player);
        });

        return HookResult.Continue;
    }

    private void OnPrecacheResource(IOnPrecacheResourceEvent e)
    {
        foreach (var runtime in itemRuntimeById.Values)
        {
            if (string.IsNullOrWhiteSpace(runtime.ModelPath))
            {
                continue;
            }

            e.AddItem(runtime.ModelPath);
        }

        if (!string.IsNullOrWhiteSpace(runtimeSettings.DefaultTModel))
        {
            e.AddItem(runtimeSettings.DefaultTModel);
        }

        if (!string.IsNullOrWhiteSpace(runtimeSettings.DefaultCtModel))
        {
            e.AddItem(runtimeSettings.DefaultCtModel);
        }
    }

    private void RegisterItemsAndHandlers()
    {
        if (shopApi == null)
        {
            return;
        }

        UnregisterItemsAndHandlers();

        if (moduleConfig == null)
        {
            LoadModuleConfig();
        }

        if (moduleConfig == null)
        {
            Core.Logger.LogWarning("PlayerModels config could not be loaded.");
            return;
        }

        runtimeSettings = moduleConfig.Settings;

        var category = string.IsNullOrWhiteSpace(moduleConfig.Settings.Category)
            ? DefaultCategory
            : moduleConfig.Settings.Category.Trim();

        if (moduleConfig.Items.Count == 0)
        {
            moduleConfig = CreateDefaultConfig();
            category = moduleConfig.Settings.Category;
            runtimeSettings = moduleConfig.Settings;
            Core.Logger.LogWarning("PlayerModels config has no items. Using in-memory defaults.");
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
                Core.Logger.LogWarning("Failed to register player model item '{ItemId}'.", definition.Id);
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
        shopApi.OnItemPreview += OnItemPreview;
        handlersRegistered = true;

        Core.Logger.LogInformation(
            "Shop_PlayerModels initialized. RegisteredItems={RegisteredItems}",
            registeredCount
        );
    }

    private void LoadModuleConfig()
    {
        serviceProvider?.Dispose();
        serviceProvider = null;

        Core.Configuration.InitializeJsonWithModel<PlayerModelsModuleConfig>(ConfigFileName, ConfigSectionName)
            .Configure(builder => builder.AddJsonFile(ConfigFileName, false, true));

        ServiceCollection services = new();
        services.AddSwiftly(Core)
                .AddOptionsWithValidateOnStart<PlayerModelsModuleConfig>()
                .BindConfiguration(ConfigSectionName);

        serviceProvider = services.BuildServiceProvider();
        moduleConfig = serviceProvider.GetRequiredService<IOptions<PlayerModelsModuleConfig>>().Value;
        NormalizeConfig(moduleConfig);
    }

    private void UnregisterItemsAndHandlers()
    {
        if (!handlersRegistered || shopApi == null)
        {
            return;
        }

        shopApi.OnBeforeItemPurchase -= OnBeforeItemPurchase;
        shopApi.OnItemToggled -= OnItemToggled;
        shopApi.OnItemSold -= OnItemSold;
        shopApi.OnItemExpired -= OnItemExpired;
        shopApi.OnItemPreview -= OnItemPreview;

        foreach (var itemId in registeredItemIds)
        {
            _ = shopApi.UnregisterItem(itemId);
        }

        registeredItemIds.Clear();
        registeredItemOrder.Clear();
        itemRuntimeById.Clear();
        ClearAllPreviewEntities();
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

        var player = context.Player;
        var loc = Core.Translation.GetPlayerLocalizer(player);
        context.Block($"{GetPrefix(player)} {loc["error.permission", shopApi?.GetItemDisplayName(player, context.Item) ?? context.Item.DisplayName, runtime.RequiredPermission]}");
    }

    private void OnItemToggled(IPlayer player, ShopItemDefinition item, bool enabled)
    {
        if (shopApi == null || !registeredItemIds.Contains(item.Id))
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

        ApplyConfiguredOrDefaultModel(player);
    }

    private void OnItemSold(IPlayer player, ShopItemDefinition item, decimal amount)
    {
        if (!registeredItemIds.Contains(item.Id))
        {
            return;
        }

        ApplyConfiguredOrDefaultModel(player);
    }

    private void OnItemExpired(IPlayer player, ShopItemDefinition item)
    {
        if (!registeredItemIds.Contains(item.Id))
        {
            return;
        }

        ApplyConfiguredOrDefaultModel(player);
    }

    private void OnItemPreview(IPlayer player, ShopItemDefinition item)
    {
        if (!registeredItemIds.Contains(item.Id))
        {
            return;
        }

        if (!itemRuntimeById.TryGetValue(item.Id, out var runtime))
        {
            return;
        }

        SpawnPreviewModel(player, runtime.ModelPath, shopApi?.GetItemDisplayName(player, item) ?? item.DisplayName);
    }

    private void ApplyConfiguredOrDefaultModel(IPlayer player)
    {
        if (shopApi == null || player == null || !player.IsValid || player.IsFakeClient)
        {
            return;
        }

        if (TryGetEnabledRuntime(player, out var runtime))
        {
            ApplyModel(player, runtime.ModelPath);
            return;
        }

        ResetToTeamDefaultModel(player);
    }

    private bool TryGetEnabledRuntime(IPlayer player, out PlayerModelItemRuntime runtime)
    {
        runtime = default;

        if (shopApi == null)
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

    private void ResetToTeamDefaultModel(IPlayer player)
    {
        var defaultModel = ResolveTeamDefaultModel(player);
        if (string.IsNullOrWhiteSpace(defaultModel))
        {
            return;
        }

        ApplyModel(player, defaultModel);
    }

    private string ResolveTeamDefaultModel(IPlayer player)
    {
        var teamNum = player.Controller.TeamNum;
        if (teamNum == (int)Team.T)
        {
            return runtimeSettings.DefaultTModel;
        }

        if (teamNum == (int)Team.CT)
        {
            return runtimeSettings.DefaultCtModel;
        }

        return string.Empty;
    }

    private void ApplyModel(IPlayer player, string modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            return;
        }

        Core.Scheduler.NextWorldUpdate(() =>
        {
            if (player == null || !player.IsValid || player.IsFakeClient)
            {
                return;
            }

            var pawn = player.PlayerPawn;
            if (pawn == null || !pawn.IsValid || pawn.LifeState != (int)LifeState_t.LIFE_ALIVE)
            {
                return;
            }

            try
            {
                // Set initial model
                pawn.SetModel(modelPath);

                // AG2 Attachment and bone scale refresh
                if (pawn.CBodyComponent?.SceneNode != null)
                {
                    pawn.AcceptInput("SetModel", modelPath);
                }

                // Force viewmodel and active weapon attachment update
                if (pawn.WeaponServices != null)
                {
                    var activeWeaponHandle = pawn.WeaponServices.ActiveWeapon;
                    if (activeWeaponHandle.IsValid)
                    {
                        var activeWeapon = activeWeaponHandle.Value;
                        if (activeWeapon != null && activeWeapon.IsValid)
                        {
                            activeWeapon.AcceptInput("SetBodygroup", "0");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Core.Logger.LogWarning(ex, "Failed to apply model '{ModelPath}' to player {PlayerId}.", modelPath, player.PlayerID);
            }
        });
    }

    private void SpawnPreviewModel(IPlayer player, string modelPath, string displayName)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            return;
        }

        Core.Scheduler.NextWorldUpdate(() =>
        {
            if (!TryGetAlivePawn(player, out var pawn))
            {
                return;
            }

            var playerId = player.PlayerID;
            var previewSession = ReservePreviewSession(playerId, out var previousPreviewEntityIndex);
            if (previousPreviewEntityIndex != 0)
            {
                DespawnPreviewEntityByIndex(previousPreviewEntityIndex);
            }

            CDynamicProp? preview = null;
            try
            {
                var origin = pawn.AbsOrigin ?? Vector.Zero;
                var rotation = pawn.AbsRotation ?? QAngle.Zero;
                var yawRadians = rotation.Y * (MathF.PI / 180f);
                var previewPosition = new Vector(
                    origin.X + (MathF.Cos(yawRadians) * PreviewDistance),
                    origin.Y + (MathF.Sin(yawRadians) * PreviewDistance),
                    origin.Z
                );
                var initialYaw = NormalizeYaw(rotation.Y + 180f);
                var previewRotation = new QAngle(rotation.X, initialYaw, rotation.Z);

                preview = Core.EntitySystem.CreateEntityByDesignerName<CDynamicProp>("prop_dynamic_override");
                if (preview == null || !preview.IsValid)
                {
                    return;
                }

                // Keep preview model facing player by default; optional full-spin is controlled by config.
                preview.Teleport(previewPosition, previewRotation, Vector.Zero);
                preview.DispatchSpawn();
                preview.SetModel(modelPath);
                ConfigurePreviewVisibility(preview, player);
                ConfigurePreviewGlow(preview);
                SetPreviewEntityIndex(playerId, previewSession, preview.Index);

                if (runtimeSettings.RotatePreviewModel)
                {
                    RotatePreviewModel(playerId, previewSession, preview, previewPosition, previewRotation);
                }

                player.SendChat($"{GetPrefix(player)} {Core.Translation.GetPlayerLocalizer(player)["preview.started", displayName, (int)PreviewDurationSeconds]}");
            }
            catch (Exception ex)
            {
                Core.Logger.LogWarning(ex, "Failed to spawn player model preview for '{DisplayName}'.", displayName);
                ClearPreviewSessionIfCurrent(playerId, previewSession);
                return;
            }

            Core.Scheduler.DelayBySeconds(PreviewDurationSeconds, () =>
            {
                Core.Scheduler.NextWorldUpdate(() =>
                {
                    if (!IsCurrentPreviewSession(playerId, previewSession))
                    {
                        return;
                    }

                    try
                    {
                        if (preview is not null && preview.IsValid)
                        {
                            preview.Despawn();
                        }
                    }
                    catch (Exception ex)
                    {
                        Core.Logger.LogWarning(ex, "Failed to despawn player model preview.");
                    }
                    finally
                    {
                        ClearPreviewSessionIfCurrent(playerId, previewSession);
                    }
                });
            });
        });
    }

    private long ReservePreviewSession(int playerId, out uint previousEntityIndex)
    {
        lock (previewSync)
        {
            previousEntityIndex = 0;
            if (previewStateByPlayerId.TryGetValue(playerId, out var previousState))
            {
                previousEntityIndex = previousState.EntityIndex;
            }

            previewSessionCounter++;
            var session = previewSessionCounter;
            previewStateByPlayerId[playerId] = new PlayerModelPreviewState(session, 0);
            return session;
        }
    }

    private void SetPreviewEntityIndex(int playerId, long sessionId, uint entityIndex)
    {
        lock (previewSync)
        {
            if (!previewStateByPlayerId.TryGetValue(playerId, out var state) || state.SessionId != sessionId)
            {
                return;
            }

            previewStateByPlayerId[playerId] = state with { EntityIndex = entityIndex };
        }
    }

    private bool IsCurrentPreviewSession(int playerId, long sessionId)
    {
        lock (previewSync)
        {
            return previewStateByPlayerId.TryGetValue(playerId, out var state) && state.SessionId == sessionId;
        }
    }

    private void ClearPreviewSessionIfCurrent(int playerId, long sessionId)
    {
        lock (previewSync)
        {
            if (previewStateByPlayerId.TryGetValue(playerId, out var state) && state.SessionId == sessionId)
            {
                previewStateByPlayerId.Remove(playerId);
            }
        }
    }

    private void DespawnPreviewEntityByIndex(uint entityIndex)
    {
        if (entityIndex == 0)
        {
            return;
        }

        try
        {
            var existing = Core.EntitySystem.GetEntityByIndex<CDynamicProp>(entityIndex);
            if (existing is not null && existing.IsValid)
            {
                existing.Despawn();
            }
        }
        catch (Exception ex)
        {
            Core.Logger.LogWarning(ex, "Failed to despawn existing preview entity index {EntityIndex}.", entityIndex);
        }
    }

    private void ClearAllPreviewEntities()
    {
        uint[] entityIndexes;
        lock (previewSync)
        {
            entityIndexes = previewStateByPlayerId.Values
                .Select(static state => state.EntityIndex)
                .Where(static index => index != 0)
                .ToArray();
            previewStateByPlayerId.Clear();
        }

        foreach (var entityIndex in entityIndexes)
        {
            DespawnPreviewEntityByIndex(entityIndex);
        }
    }

    private string GetPrefix(IPlayer player)
    {
        var loc = Core.Translation.GetPlayerLocalizer(player);
        if (runtimeSettings.UseCorePrefix)
        {
            var corePrefix = shopApi?.GetShopPrefix(player);
            if (!string.IsNullOrWhiteSpace(corePrefix))
            {
                return corePrefix;
            }
        }

        return loc["shop.prefix"];
    }

    private void ConfigurePreviewVisibility(CDynamicProp preview, IPlayer previewOwner)
    {
        if (preview == null || !preview.IsValid || previewOwner == null || !previewOwner.IsValid)
        {
            return;
        }

        if (previewOwner.PlayerID < 0)
        {
            return;
        }

        try
        {
            // Hide for everyone, then explicitly allow only the preview owner.
            preview.SetTransmitState(false);
            preview.SetTransmitState(true, previewOwner.PlayerID);
        }
        catch (Exception ex)
        {
            Core.Logger.LogWarning(ex, "Failed to configure preview visibility for player {PlayerId}.", previewOwner.PlayerID);
        }
    }

    private void ConfigurePreviewGlow(CDynamicProp preview)
    {
        if (preview == null || !preview.IsValid)
        {
            return;
        }

        try
        {
            // Dynamic prop glow properties.
            preview.InitialGlowState = 1;
            preview.GlowRangeMin = 0;
            preview.GlowRange = 2048;
            preview.GlowTeam = -1;
            preview.GlowColor = new Color(255, 180, 40, 255);

            // Base model glow properties.
            var glow = preview.Glow;
            glow.GlowType = 3;
            glow.GlowTeam = -1;
            glow.GlowRangeMin = 0;
            glow.GlowRange = 2048;
            glow.GlowColorOverride = new Color(255, 180, 40, 255);
            glow.GlowColorOverrideUpdated();
            glow.GlowTypeUpdated();
            glow.GlowTeamUpdated();
            glow.GlowRangeMinUpdated();
            glow.GlowRangeUpdated();

            glow.EligibleForScreenHighlight = true;
            glow.EligibleForScreenHighlightUpdated();
            glow.Glowing = true;
            preview.GlowUpdated();
        }
        catch (Exception ex)
        {
            Core.Logger.LogWarning(ex, "Failed to configure glow on preview model entity.");
        }
    }

    private void RotatePreviewModel(int playerId, long previewSession, CDynamicProp preview, Vector origin, QAngle initialRotation)
    {
        var steps = (int)MathF.Ceiling(PreviewDurationSeconds / PreviewRotateIntervalSeconds);
        if (steps <= 0)
        {
            return;
        }

        for (var step = 1; step <= steps; step++)
        {
            var currentStep = step;
            var progress = currentStep / (float)steps;
            var targetYaw = NormalizeYaw(initialRotation.Y + (360f * progress));

            Core.Scheduler.DelayBySeconds(currentStep * PreviewRotateIntervalSeconds, () =>
            {
                Core.Scheduler.NextWorldUpdate(() =>
                {
                    if (!IsCurrentPreviewSession(playerId, previewSession))
                    {
                        return;
                    }

                    if (preview == null || !preview.IsValid)
                    {
                        return;
                    }

                    try
                    {
                        preview.Teleport(origin, new QAngle(initialRotation.X, targetYaw, initialRotation.Z), Vector.Zero);
                    }
                    catch (Exception ex)
                    {
                        Core.Logger.LogWarning(ex, "Failed rotating preview player model entity.");
                    }
                });
            });
        }
    }

    private static float NormalizeYaw(float yaw)
    {
        var normalized = yaw % 360f;
        return normalized < 0f ? normalized + 360f : normalized;
    }

    private static bool TryGetAlivePawn(IPlayer player, out CCSPlayerPawn pawn)
    {
        pawn = null!;

        if (player == null || !player.IsValid || player.IsFakeClient)
        {
            return false;
        }

        var playerPawn = player.PlayerPawn;
        if (playerPawn == null || !playerPawn.IsValid || playerPawn.LifeState != (int)LifeState_t.LIFE_ALIVE)
        {
            return false;
        }

        pawn = playerPawn;
        return true;
    }

    private bool TryCreateDefinition(
        PlayerModelItemTemplate itemTemplate,
        string category,
        out ShopItemDefinition definition,
        out PlayerModelItemRuntime runtime)
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

        if (string.IsNullOrWhiteSpace(itemTemplate.ModelPath))
        {
            Core.Logger.LogWarning("Skipping item '{ItemId}' because ModelPath is empty.", itemId);
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
                "Skipping item '{ItemId}' because player model items cannot use Type '{Type}'.",
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
            CanBeSold: itemTemplate.CanBeSold,
            DisplayNameResolver: player => ResolveDisplayName(itemTemplate, player)
        );

        runtime = new PlayerModelItemRuntime(
            ItemId: itemId,
            ModelPath: itemTemplate.ModelPath.Trim(),
            RequiredPermission: itemTemplate.RequiredPermission?.Trim() ?? string.Empty
        );

        return true;
    }

    private string ResolveDisplayName(PlayerModelItemTemplate itemTemplate, IPlayer? player = null)
    {
        if (!string.IsNullOrWhiteSpace(itemTemplate.DisplayNameKey))
        {
            var key = itemTemplate.DisplayNameKey.Trim();
            var localizer = player == null ? Core.Localizer : Core.Translation.GetPlayerLocalizer(player);
            var localized = itemTemplate.Type.Equals(nameof(ShopItemType.Permanent), StringComparison.OrdinalIgnoreCase)
                ? localizer[key, itemTemplate.ModelName]
                : localizer[key, itemTemplate.ModelName, FormatDuration(itemTemplate.DurationSeconds)];

            if (!string.Equals(localized, key, StringComparison.Ordinal))
            {
                return localized;
            }
        }

        if (!string.IsNullOrWhiteSpace(itemTemplate.DisplayName))
        {
            return itemTemplate.DisplayName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(itemTemplate.ModelName))
        {
            return itemTemplate.ModelName.Trim();
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

    private static void NormalizeConfig(PlayerModelsModuleConfig config)
    {
        config.Settings ??= new PlayerModelsModuleSettings();
        config.Items ??= [];

        config.Settings.Category = string.IsNullOrWhiteSpace(config.Settings.Category)
            ? DefaultCategory
            : config.Settings.Category.Trim();

        config.Settings.DefaultTModel = string.IsNullOrWhiteSpace(config.Settings.DefaultTModel)
            ? "agents/models/tm_phoenix/tm_phoenix.vmdl"
            : config.Settings.DefaultTModel.Trim();

        config.Settings.DefaultCtModel = string.IsNullOrWhiteSpace(config.Settings.DefaultCtModel)
            ? "agents/models/ctm_sas/ctm_sas.vmdl"
            : config.Settings.DefaultCtModel.Trim();
    }

    private static PlayerModelsModuleConfig CreateDefaultConfig()
    {
        return new PlayerModelsModuleConfig
        {
            Settings = new PlayerModelsModuleSettings
            {
                Category = DefaultCategory,
                DefaultTModel = "agents/models/tm_phoenix/tm_phoenix.vmdl",
                DefaultCtModel = "agents/models/ctm_sas/ctm_sas.vmdl",
                RotatePreviewModel = true
            },
            Items =
            [
                new PlayerModelItemTemplate
                {
                    Id = "model_frogman_hourly",
                    ModelName = "Frogman",
                    DisplayNameKey = "item.temporary.name",
                    ModelPath = "agents/models/ctm_diver/ctm_diver_variantb.vmdl",
                    Price = 3500,
                    SellPrice = 1750,
                    DurationSeconds = 3600,
                    Type = nameof(ShopItemType.Temporary),
                    Team = nameof(ShopItemTeam.Any),
                    Enabled = true,
                    CanBeSold = true
                },
                new PlayerModelItemTemplate
                {
                    Id = "model_fbi_permanent",
                    ModelName = "FBI",
                    DisplayNameKey = "item.permanent.name",
                    ModelPath = "agents/models/ctm_fbi/ctm_fbi_varianta.vmdl",
                    Price = 9000,
                    SellPrice = 4500,
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

internal readonly record struct PlayerModelItemRuntime(
    string ItemId,
    string ModelPath,
    string RequiredPermission
);

internal readonly record struct PlayerModelPreviewState(
    long SessionId,
    uint EntityIndex
);

internal sealed class PlayerModelsModuleConfig
{
    public PlayerModelsModuleSettings Settings { get; set; } = new();
    public List<PlayerModelItemTemplate> Items { get; set; } = [];
}

internal sealed class PlayerModelsModuleSettings
{
    public bool UseCorePrefix { get; set; } = true;
    public string Category { get; set; } = "Visuals/Player Models";
    public string DefaultTModel { get; set; } = "agents/models/tm_phoenix/tm_phoenix.vmdl";
    public string DefaultCtModel { get; set; } = "agents/models/ctm_sas/ctm_sas.vmdl";
    public bool RotatePreviewModel { get; set; } = true;
}

internal sealed class PlayerModelItemTemplate
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string DisplayNameKey { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public string ModelPath { get; set; } = string.Empty;
    public int Price { get; set; } = 0;
    public int? SellPrice { get; set; }
    public int DurationSeconds { get; set; } = 0;
    public string Type { get; set; } = nameof(ShopItemType.Temporary);
    public string Team { get; set; } = nameof(ShopItemTeam.Any);
    public bool Enabled { get; set; } = true;
    public bool CanBeSold { get; set; } = true;
    public string RequiredPermission { get; set; } = string.Empty;
}
