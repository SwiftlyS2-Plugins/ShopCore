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
    Id = "Shop_PlayerModels",
    Name = "Shop PlayerModels",
    Author = "T3Marius",
    Version = "1.0.0",
    Description = "ShopCore module with player model items"
)]
public class Shop_PlayerModels : BasePlugin
{
    private const string ShopCoreInterfaceKey = "ShopCore.API.v1";
    private const string ModulePluginId = "Shop_PlayerModels";
    private const string TemplateFileName = "playermodels_config.jsonc";
    private const string TemplateSectionName = "Main";
    private const string DefaultCategory = "Visuals/Player Models";
    private const float PreviewDurationSeconds = 8f;
    private const float PreviewDistance = 75f;
    private const float PreviewRotateIntervalSeconds = 0.05f;

    private IShopCoreApiV1? shopApi;
    private bool handlersRegistered;

    private readonly HashSet<string> registeredItemIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> registeredItemOrder = new();
    private readonly Dictionary<string, PlayerModelItemRuntime> itemRuntimeById = new(StringComparer.OrdinalIgnoreCase);

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
            Core.Logger.LogWarning("ShopCore API is not available. PlayerModels items will not be registered.");
            return;
        }

        if (!handlersRegistered)
        {
            RegisterItemsAndHandlers();
        }
    }

    public override void Load(bool hotReload)
    {
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
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerSpawn(EventPlayerSpawn e)
    {
        var player = Core.PlayerManager.GetPlayer(e.UserId);
        if (player is null || !player.IsValid || player.IsFakeClient)
        {
            return HookResult.Continue;
        }

        Core.Scheduler.NextWorldUpdate(() =>
        {
            if (player is null || !player.IsValid || player.IsFakeClient)
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
        if (shopApi is null)
        {
            return;
        }

        UnregisterItemsAndHandlers();

        var moduleConfig = shopApi.LoadModuleConfig<PlayerModelsModuleConfig>(
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
        shopApi.OnItemPreview -= OnItemPreview;

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

        var player = context.Player;
        var loc = Core.Translation.GetPlayerLocalizer(player);
        var prefix = loc["shop.prefix"];
        context.Block($"{prefix} {loc["module.player_models.error.permission", context.Item.DisplayName, runtime.RequiredPermission]}");
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

        SpawnPreviewModel(player, runtime.ModelPath, item.DisplayName);
    }

    private void ApplyConfiguredOrDefaultModel(IPlayer player)
    {
        if (shopApi is null || player is null || !player.IsValid || player.IsFakeClient)
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
            if (player is null || !player.IsValid || player.IsFakeClient)
            {
                return;
            }

            var pawn = player.PlayerPawn;
            if (pawn is null || !pawn.IsValid || pawn.LifeState != (int)LifeState_t.LIFE_ALIVE)
            {
                return;
            }

            try
            {
                pawn.SetModel(modelPath);
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
                if (preview is null || !preview.IsValid)
                {
                    return;
                }

                // Keep preview model facing player by default; optional full-spin is controlled by config.
                preview.Teleport(previewPosition, previewRotation, Vector.Zero);
                preview.DispatchSpawn();
                preview.SetModel(modelPath);
                ConfigurePreviewVisibility(preview, player);
                ConfigurePreviewGlow(preview);

                if (runtimeSettings.RotatePreviewModel)
                {
                    RotatePreviewModel(preview, previewPosition, previewRotation);
                }

                player.SendChat($"{Core.Localizer["shop.prefix"]} {Core.Localizer["module.player_models.preview.started", displayName, (int)PreviewDurationSeconds]}");
            }
            catch (Exception ex)
            {
                Core.Logger.LogWarning(ex, "Failed to spawn player model preview for '{DisplayName}'.", displayName);
                return;
            }

            Core.Scheduler.DelayBySeconds(PreviewDurationSeconds, () =>
            {
                Core.Scheduler.NextWorldUpdate(() =>
                {
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
                });
            });
        });
    }

    private void ConfigurePreviewVisibility(CDynamicProp preview, IPlayer previewOwner)
    {
        if (preview is null || !preview.IsValid || previewOwner is null || !previewOwner.IsValid)
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
        if (preview is null || !preview.IsValid)
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

    private void RotatePreviewModel(CDynamicProp preview, Vector origin, QAngle initialRotation)
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
                    if (preview is null || !preview.IsValid)
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

        if (player is null || !player.IsValid || player.IsFakeClient)
        {
            return false;
        }

        var playerPawn = player.PlayerPawn;
        if (playerPawn is null || !playerPawn.IsValid || playerPawn.LifeState != (int)LifeState_t.LIFE_ALIVE)
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
            CanBeSold: itemTemplate.CanBeSold
        );

        runtime = new PlayerModelItemRuntime(
            ItemId: itemId,
            ModelPath: itemTemplate.ModelPath.Trim(),
            RequiredPermission: itemTemplate.RequiredPermission?.Trim() ?? string.Empty
        );

        return true;
    }

    private string ResolveDisplayName(PlayerModelItemTemplate itemTemplate)
    {
        if (!string.IsNullOrWhiteSpace(itemTemplate.DisplayNameKey))
        {
            var key = itemTemplate.DisplayNameKey.Trim();
            var localized = itemTemplate.Type.Equals(nameof(ShopItemType.Permanent), StringComparison.OrdinalIgnoreCase)
                ? Core.Localizer[key, itemTemplate.ModelName]
                : Core.Localizer[key, itemTemplate.ModelName, FormatDuration(itemTemplate.DurationSeconds)];

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
            ? "characters/models/tm_phoenix/tm_phoenix.vmdl"
            : config.Settings.DefaultTModel.Trim();

        config.Settings.DefaultCtModel = string.IsNullOrWhiteSpace(config.Settings.DefaultCtModel)
            ? "characters/models/ctm_sas/ctm_sas.vmdl"
            : config.Settings.DefaultCtModel.Trim();
    }

    private static PlayerModelsModuleConfig CreateDefaultConfig()
    {
        return new PlayerModelsModuleConfig
        {
            Settings = new PlayerModelsModuleSettings
            {
                Category = DefaultCategory,
                DefaultTModel = "characters/models/tm_phoenix/tm_phoenix.vmdl",
                DefaultCtModel = "characters/models/ctm_sas/ctm_sas.vmdl",
                RotatePreviewModel = true
            },
            Items =
            [
                new PlayerModelItemTemplate
                {
                    Id = "model_frogman_hourly",
                    ModelName = "Frogman",
                    DisplayNameKey = "module.player_models.item.temporary.name",
                    ModelPath = "characters/models/ctm_diver/ctm_diver_variantb.vmdl",
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
                    DisplayNameKey = "module.player_models.item.permanent.name",
                    ModelPath = "characters/models/ctm_fbi/ctm_fbi_varianta.vmdl",
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

internal sealed class PlayerModelsModuleConfig
{
    public PlayerModelsModuleSettings Settings { get; set; } = new();
    public List<PlayerModelItemTemplate> Items { get; set; } = [];
}

internal sealed class PlayerModelsModuleSettings
{
    public string Category { get; set; } = "Visuals/Player Models";
    public string DefaultTModel { get; set; } = "characters/models/tm_phoenix/tm_phoenix.vmdl";
    public string DefaultCtModel { get; set; } = "characters/models/ctm_sas/ctm_sas.vmdl";
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
