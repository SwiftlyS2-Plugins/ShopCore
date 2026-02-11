using Microsoft.Extensions.Logging;
using ShopCore.Contract;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameEvents;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Plugins;
using SwiftlyS2.Shared.SchemaDefinitions;

namespace ShopCore;

[PluginMetadata(
    Id = "Shop_Tracers",
    Name = "Shop Tracers",
    Author = "T3Marius",
    Version = "1.0.0",
    Description = "ShopCore module with bullet tracer items"
)]
public class Shop_Tracers : BasePlugin
{
    private const string ShopCoreInterfaceKey = "ShopCore.API.v1";
    private const string ModulePluginId = "Shop_Tracers";
    private const string TemplateFileName = "tracers_config.jsonc";
    private const string TemplateSectionName = "Main";
    private const string DefaultCategory = "Visuals/Tracers";

    private static readonly Color TeamTColor = new(255, 220, 50, 255);
    private static readonly Color TeamCtColor = new(80, 170, 255, 255);

    private IShopCoreApiV1? shopApi;
    private bool handlersRegistered;

    private readonly HashSet<string> registeredItemIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> registeredItemOrder = new();
    private readonly Dictionary<string, TracerItemRuntime> itemRuntimeById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Random random = new();

    private TracersModuleSettings runtimeSettings = new();

    public Shop_Tracers(ISwiftlyCore core) : base(core)
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
            Core.Logger.LogWarning("ShopCore API is not available. Tracer items will not be registered.");
            return;
        }

        if (!handlersRegistered)
        {
            RegisterItemsAndHandlers();
        }
    }

    public override void Load(bool hotReload)
    {
        if (shopApi is not null && !handlersRegistered)
        {
            RegisterItemsAndHandlers();
        }
    }

    public override void Unload()
    {
        UnregisterItemsAndHandlers();
    }

    [GameEventHandler(HookMode.Pre)]
    public HookResult OnBulletImpact(EventBulletImpact e)
    {
        if (shopApi is null || !handlersRegistered)
        {
            return HookResult.Continue;
        }

        var player = e.UserIdPlayer;
        if (player is null || !player.IsValid || player.IsFakeClient)
        {
            return HookResult.Continue;
        }

        if (!TryGetEnabledRuntime(player, out var runtime))
        {
            return HookResult.Continue;
        }

        if (!TryGetTracerStart(player, runtime.OriginZOffset, out var start))
        {
            return HookResult.Continue;
        }

        var end = new Vector(e.X, e.Y, e.Z);
        var color = ResolveTracerColor(player, runtime);

        Core.Scheduler.NextWorldUpdate(() => DrawTracer(start, end, color, runtime));
        return HookResult.Continue;
    }

    private void RegisterItemsAndHandlers()
    {
        if (shopApi is null)
        {
            return;
        }

        UnregisterItemsAndHandlers();

        var moduleConfig = shopApi.LoadModuleConfig<TracersModuleConfig>(
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
            if (!TryCreateDefinition(itemTemplate, moduleConfig.Settings, category, out var definition, out var runtime))
            {
                continue;
            }

            if (!shopApi.RegisterItem(definition))
            {
                Core.Logger.LogWarning("Failed to register tracer item '{ItemId}'.", definition.Id);
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
            "Shop_Tracers initialized. RegisteredItems={RegisteredItems}",
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
            "module.tracers.error.permission",
            context.Item.DisplayName,
            runtime.RequiredPermission
        );
    }

    private void OnItemToggled(IPlayer player, ShopItemDefinition item, bool enabled)
    {
        if (!enabled || shopApi is null || !registeredItemIds.Contains(item.Id))
        {
            return;
        }

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

    private void OnItemSold(IPlayer player, ShopItemDefinition item, decimal amount)
    {
    }

    private void OnItemExpired(IPlayer player, ShopItemDefinition item)
    {
    }

    private bool TryGetEnabledRuntime(IPlayer player, out TracerItemRuntime runtime)
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

    private static bool TryGetTracerStart(IPlayer player, float zOffset, out Vector start)
    {
        start = Vector.Zero;

        var pawn = player.PlayerPawn;
        if (pawn is null || !pawn.IsValid)
        {
            return false;
        }

        var origin = pawn.AbsOrigin;
        if (origin is null)
        {
            return false;
        }

        start = new Vector(origin.Value.X, origin.Value.Y, origin.Value.Z + zOffset);
        return true;
    }

    private Color ResolveTracerColor(IPlayer player, TracerItemRuntime runtime)
    {
        return runtime.ColorMode switch
        {
            TracerColorMode.Random => NextRandomColor(),
            TracerColorMode.Team => ResolveTeamColor(player),
            _ => runtime.StaticColor
        };
    }

    private Color ResolveTeamColor(IPlayer player)
    {
        return player.Controller.TeamNum == (int)Team.CT ? TeamCtColor : TeamTColor;
    }

    private Color NextRandomColor()
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

    private void DrawTracer(Vector start, Vector end, Color color, TracerItemRuntime runtime)
    {
        try
        {
            var beam = Core.EntitySystem.CreateEntityByDesignerName<CBeam>("beam");
            if (beam is null || !beam.IsValid)
            {
                return;
            }

            beam.Render = color;
            beam.RenderUpdated();

            beam.Width = runtime.StartWidth;
            beam.WidthUpdated();

            beam.EndWidth = runtime.EndWidth;
            beam.EndWidthUpdated();

            beam.TurnedOff = false;
            beam.TurnedOffUpdated();

            beam.Teleport(start, QAngle.Zero, Vector.Zero);

            beam.EndPos.X = end.X;
            beam.EndPos.Y = end.Y;
            beam.EndPos.Z = end.Z;
            beam.EndPosUpdated();

            beam.DispatchSpawn();

            Core.Scheduler.DelayBySeconds(runtime.LifeSeconds, () =>
            {
                Core.Scheduler.NextWorldUpdate(() =>
                {
                    try
                    {
                        if (beam.IsValid)
                        {
                            beam.Despawn();
                        }
                    }
                    catch (Exception ex)
                    {
                        Core.Logger.LogWarning(ex, "Failed to despawn tracer beam entity.");
                    }
                });
            });
        }
        catch (Exception ex)
        {
            Core.Logger.LogWarning(ex, "Failed to draw tracer beam.");
        }
    }

    private bool TryCreateDefinition(
        TracerItemTemplate itemTemplate,
        TracersModuleSettings settings,
        string category,
        out ShopItemDefinition definition,
        out TracerItemRuntime runtime)
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
                "Skipping item '{ItemId}' because tracer items cannot use Type '{Type}'.",
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

        if (!TryResolveColorMode(itemTemplate.Color, out var colorMode, out var staticColor))
        {
            Core.Logger.LogWarning(
                "Skipping item '{ItemId}' because Color '{Color}' is invalid.",
                itemId,
                itemTemplate.Color
            );
            return false;
        }

        var lifeSeconds = itemTemplate.LifeSeconds ?? settings.DefaultLifeSeconds;
        if (lifeSeconds <= 0f)
        {
            lifeSeconds = 0.3f;
        }

        var startWidth = itemTemplate.StartWidth ?? settings.DefaultStartWidth;
        if (startWidth <= 0f)
        {
            startWidth = 1.0f;
        }

        var endWidth = itemTemplate.EndWidth ?? settings.DefaultEndWidth;
        if (endWidth <= 0f)
        {
            endWidth = 0.5f;
        }

        var originZOffset = itemTemplate.OriginZOffset ?? settings.DefaultOriginZOffset;

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

        runtime = new TracerItemRuntime(
            ItemId: itemId,
            ColorMode: colorMode,
            StaticColor: staticColor,
            LifeSeconds: lifeSeconds,
            StartWidth: startWidth,
            EndWidth: endWidth,
            OriginZOffset: originZOffset,
            RequiredPermission: itemTemplate.RequiredPermission?.Trim() ?? string.Empty
        );

        return true;
    }

    private string ResolveDisplayName(TracerItemTemplate itemTemplate)
    {
        var colorName = string.IsNullOrWhiteSpace(itemTemplate.ColorDisplayName)
            ? itemTemplate.Color
            : itemTemplate.ColorDisplayName;

        if (!string.IsNullOrWhiteSpace(itemTemplate.DisplayNameKey))
        {
            var key = itemTemplate.DisplayNameKey.Trim();
            var localized = itemTemplate.Type.Equals(nameof(ShopItemType.Permanent), StringComparison.OrdinalIgnoreCase)
                ? Core.Localizer[key, colorName]
                : Core.Localizer[key, colorName, FormatDuration(itemTemplate.DurationSeconds)];

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

    private static bool TryResolveColorMode(string? value, out TracerColorMode mode, out Color color)
    {
        mode = TracerColorMode.Static;
        color = new Color(255, 255, 255, 255);

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        if (text.Equals("random", StringComparison.OrdinalIgnoreCase))
        {
            mode = TracerColorMode.Random;
            return true;
        }

        if (text.Equals("team", StringComparison.OrdinalIgnoreCase))
        {
            mode = TracerColorMode.Team;
            return true;
        }

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

    private static void NormalizeConfig(TracersModuleConfig config)
    {
        config.Settings ??= new TracersModuleSettings();
        config.Items ??= [];

        config.Settings.Category = string.IsNullOrWhiteSpace(config.Settings.Category)
            ? DefaultCategory
            : config.Settings.Category.Trim();

        if (config.Settings.DefaultLifeSeconds <= 0f)
        {
            config.Settings.DefaultLifeSeconds = 0.3f;
        }

        if (config.Settings.DefaultStartWidth <= 0f)
        {
            config.Settings.DefaultStartWidth = 1.0f;
        }

        if (config.Settings.DefaultEndWidth <= 0f)
        {
            config.Settings.DefaultEndWidth = 0.5f;
        }
    }

    private static TracersModuleConfig CreateDefaultConfig()
    {
        return new TracersModuleConfig
        {
            Settings = new TracersModuleSettings
            {
                Category = DefaultCategory,
                DefaultLifeSeconds = 0.3f,
                DefaultStartWidth = 1.0f,
                DefaultEndWidth = 0.5f,
                DefaultOriginZOffset = 57f
            },
            Items =
            [
                new TracerItemTemplate
                {
                    Id = "red_tracer_hourly",
                    Color = "Red",
                    ColorDisplayName = "Red",
                    DisplayNameKey = "module.tracers.item.temporary.name",
                    Price = 1250,
                    SellPrice = 625,
                    DurationSeconds = 3600,
                    Type = nameof(ShopItemType.Temporary),
                    Team = nameof(ShopItemTeam.Any),
                    Enabled = true,
                    CanBeSold = true
                },
                new TracerItemTemplate
                {
                    Id = "green_tracer_hourly",
                    Color = "Green",
                    ColorDisplayName = "Green",
                    DisplayNameKey = "module.tracers.item.temporary.name",
                    Price = 1250,
                    SellPrice = 625,
                    DurationSeconds = 3600,
                    Type = nameof(ShopItemType.Temporary),
                    Team = nameof(ShopItemTeam.Any),
                    Enabled = true,
                    CanBeSold = true
                },
                new TracerItemTemplate
                {
                    Id = "team_tracer_hourly",
                    Color = "Team",
                    ColorDisplayName = "Team",
                    DisplayNameKey = "module.tracers.item.temporary.name",
                    Price = 2000,
                    SellPrice = 1000,
                    DurationSeconds = 3600,
                    Type = nameof(ShopItemType.Temporary),
                    Team = nameof(ShopItemTeam.Any),
                    Enabled = true,
                    CanBeSold = true
                },
                new TracerItemTemplate
                {
                    Id = "random_tracer_hourly",
                    Color = "Random",
                    ColorDisplayName = "Random",
                    DisplayNameKey = "module.tracers.item.temporary.name",
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

internal enum TracerColorMode
{
    Static = 0,
    Team = 1,
    Random = 2
}

internal readonly record struct TracerItemRuntime(
    string ItemId,
    TracerColorMode ColorMode,
    Color StaticColor,
    float LifeSeconds,
    float StartWidth,
    float EndWidth,
    float OriginZOffset,
    string RequiredPermission
);

internal sealed class TracersModuleConfig
{
    public TracersModuleSettings Settings { get; set; } = new();
    public List<TracerItemTemplate> Items { get; set; } = [];
}

internal sealed class TracersModuleSettings
{
    public string Category { get; set; } = "Visuals/Tracers";
    public float DefaultLifeSeconds { get; set; } = 0.3f;
    public float DefaultStartWidth { get; set; } = 1.0f;
    public float DefaultEndWidth { get; set; } = 0.5f;
    public float DefaultOriginZOffset { get; set; } = 57f;
}

internal sealed class TracerItemTemplate
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string DisplayNameKey { get; set; } = string.Empty;
    public string Color { get; set; } = "White";
    public string ColorDisplayName { get; set; } = string.Empty;
    public int Price { get; set; } = 0;
    public int? SellPrice { get; set; }
    public int DurationSeconds { get; set; } = 0;
    public string Type { get; set; } = nameof(ShopItemType.Temporary);
    public string Team { get; set; } = nameof(ShopItemTeam.Any);
    public bool Enabled { get; set; } = true;
    public bool CanBeSold { get; set; } = true;
    public float? LifeSeconds { get; set; }
    public float? StartWidth { get; set; }
    public float? EndWidth { get; set; }
    public float? OriginZOffset { get; set; }
    public string RequiredPermission { get; set; } = string.Empty;
}

