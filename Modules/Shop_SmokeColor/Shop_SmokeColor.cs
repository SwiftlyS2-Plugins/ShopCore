using Microsoft.Extensions.Logging;
using ShopCore.Contract;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Plugins;
using SwiftlyS2.Shared.SchemaDefinitions;

namespace ShopCore;

[PluginMetadata(
    Id = "Shop_SmokeColor",
    Name = "Shop SmokeColor",
    Author = "T3Marius",
    Version = "1.0.0",
    Description = "ShopCore module with smoke color items"
)]
public class Shop_SmokeColor : BasePlugin
{
    private const string ShopCoreInterfaceKey = "ShopCore.API.v1";
    private const string ModulePluginId = "Shop_SmokeColor";
    private const string TemplateFileName = "smokecolor_config.jsonc";
    private const string TemplateSectionName = "Main";
    private const string DefaultCategory = "Visuals/Smoke Colors";

    private IShopCoreApiV1? shopApi;
    private bool handlersRegistered;
    private const float PreviewDurationSeconds = 15f;
    private readonly HashSet<string> registeredItemIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> registeredItemOrder = new();
    private readonly Dictionary<string, Vector> itemColorsById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, SmokePreviewState> previewColorByPlayerId = new();

    public Shop_SmokeColor(ISwiftlyCore core) : base(core) { }

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
            Core.Logger.LogWarning("ShopCore API is not available. SmokeColor items will not be registered.");
            return;
        }

        if (!handlersRegistered)
        {
            RegisterItemsAndHandlers();
        }
    }

    public override void Load(bool hotReload)
    {
        Core.Event.OnEntityCreated += OnEntityCreated;
        Core.Event.OnClientDisconnected += OnClientDisconnected;

        if (shopApi is not null && !handlersRegistered)
        {
            RegisterItemsAndHandlers();
        }
    }

    public override void Unload()
    {
        Core.Event.OnEntityCreated -= OnEntityCreated;
        Core.Event.OnClientDisconnected -= OnClientDisconnected;
        UnregisterItemsAndHandlers();
        previewColorByPlayerId.Clear();
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent e)
    {
        previewColorByPlayerId.Remove(e.PlayerId);
    }

    private void OnEntityCreated(IOnEntityCreatedEvent e)
    {
        if (!handlersRegistered || shopApi is null)
        {
            return;
        }

        if (!string.Equals(e.Entity.DesignerName, "smokegrenade_projectile", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var entityIndex = e.Entity.Index;
        Core.Scheduler.NextWorldUpdate(() => TryApplySmokeColor(entityIndex));
    }

    private void TryApplySmokeColor(uint entityIndex)
    {
        if (shopApi is null)
        {
            return;
        }

        try
        {
            var smoke = Core.EntitySystem.GetEntityByIndex<CSmokeGrenadeProjectile>(entityIndex);
            if (smoke is null || !smoke.IsValid)
            {
                return;
            }

            var throwerPawn = smoke.Thrower.Value;
            if (throwerPawn == null || !throwerPawn.IsValid)
            {
                return;
            }

            var player = Core.PlayerManager.GetPlayerFromPawn(throwerPawn);
            if (player == null || player.IsFakeClient || !player.IsValid)
            {
                return;
            }

            if (!TryGetEnabledSmokeColor(player, out var color) && !TryGetPreviewSmokeColor(player, out color))
            {
                return;
            }

            smoke.SmokeColor = color;
            smoke.SmokeColorUpdated();
        }
        catch (Exception ex)
        {
            Core.Logger.LogWarning(ex, "Failed to apply smoke color for entity index {EntityIndex}.", entityIndex);
        }
    }

    private bool TryGetEnabledSmokeColor(IPlayer player, out Vector color)
    {
        color = Vector.Zero;

        if (shopApi is null)
        {
            return false;
        }

        foreach (var itemId in registeredItemOrder)
        {
            if (!itemColorsById.TryGetValue(itemId, out var itemColor))
            {
                continue;
            }

            if (!shopApi.IsItemEnabled(player, itemId))
            {
                continue;
            }

            color = itemColor;
            return true;
        }

        return false;
    }

    private void RegisterItemsAndHandlers()
    {
        if (shopApi is null)
        {
            return;
        }

        UnregisterItemsAndHandlers();

        var moduleConfig = shopApi.LoadModuleConfig<SmokeColorModuleConfig>(
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
            if (!TryCreateDefinition(itemTemplate, category, out var definition, out var color))
            {
                continue;
            }

            if (!shopApi.RegisterItem(definition))
            {
                Core.Logger.LogWarning("Failed to register smokecolor item '{ItemId}'.", definition.Id);
                continue;
            }

            _ = registeredItemIds.Add(definition.Id);
            registeredItemOrder.Add(definition.Id);
            itemColorsById[definition.Id] = color;
            registeredCount++;
        }

        shopApi.OnItemToggled += OnItemToggled;
        shopApi.OnItemPreview += OnItemPreview;
        handlersRegistered = true;

        Core.Logger.LogInformation(
            "Shop_SmokeColor initialized. RegisteredItems={RegisteredItems}",
            registeredCount
        );
    }

    private void UnregisterItemsAndHandlers()
    {
        if (!handlersRegistered || shopApi is null)
        {
            return;
        }

        shopApi.OnItemToggled -= OnItemToggled;
        shopApi.OnItemPreview -= OnItemPreview;

        foreach (var itemId in registeredItemIds)
        {
            _ = shopApi.UnregisterItem(itemId);
        }

        registeredItemIds.Clear();
        registeredItemOrder.Clear();
        itemColorsById.Clear();
        handlersRegistered = false;
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

    private void OnItemPreview(IPlayer player, ShopItemDefinition item)
    {
        if (!registeredItemIds.Contains(item.Id))
        {
            return;
        }

        if (!itemColorsById.TryGetValue(item.Id, out var color))
        {
            return;
        }

        var playerId = player.PlayerID;
        if (playerId < 0)
        {
            return;
        }

        previewColorByPlayerId[playerId] = new SmokePreviewState(
            Color: color,
            ExpiresAt: Core.Engine.GlobalVars.CurrentTime + PreviewDurationSeconds
        );

        Core.Scheduler.NextWorldUpdate(() =>
        {
            if (!player.IsValid || player.IsFakeClient)
            {
                return;
            }

            player.SendChat(
                $"{Core.Localizer["shop.prefix"]} {Core.Localizer["module.smoke_color.preview.started", item.DisplayName, (int)PreviewDurationSeconds]}"
            );
        });
    }

    private bool TryGetPreviewSmokeColor(IPlayer player, out Vector color)
    {
        color = Vector.Zero;

        if (!previewColorByPlayerId.TryGetValue(player.PlayerID, out var preview))
        {
            return false;
        }

        if (Core.Engine.GlobalVars.CurrentTime > preview.ExpiresAt)
        {
            previewColorByPlayerId.Remove(player.PlayerID);
            return false;
        }

        color = preview.Color;
        return true;
    }

    private bool TryCreateDefinition(
        SmokeColorItemTemplate itemTemplate,
        string category,
        out ShopItemDefinition definition,
        out Vector color)
    {
        definition = default!;
        color = Vector.Zero;

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
                "Skipping item '{ItemId}' because smoke color items cannot use Type '{Type}'.",
                itemId,
                itemType
            );
            return false;
        }

        if (!Enum.TryParse(itemTemplate.Team, ignoreCase: true, out ShopItemTeam team))
        {
            team = ShopItemTeam.Any;
        }

        if (!TryGetColorVector(itemTemplate, out color))
        {
            Core.Logger.LogWarning("Skipping item '{ItemId}' because Color must contain at least 3 integer values (R,G,B).", itemId);
            return false;
        }

        TimeSpan? duration = null;
        if (itemTemplate.DurationSeconds > 0)
        {
            duration = TimeSpan.FromSeconds(itemTemplate.DurationSeconds);
        }

        decimal? sellPrice = null;
        if (itemTemplate.SellPrice.HasValue && itemTemplate.SellPrice.Value >= 0)
        {
            sellPrice = itemTemplate.SellPrice.Value;
        }

        if (itemType == ShopItemType.Temporary && !duration.HasValue)
        {
            Core.Logger.LogWarning(
                "Skipping item '{ItemId}' because Temporary items require DurationSeconds > 0.",
                itemId
            );
            return false;
        }

        definition = new ShopItemDefinition(
            Id: itemId,
            DisplayName: ResolveDisplayName(Core, itemTemplate),
            Category: category,
            Price: itemTemplate.Price,
            SellPrice: sellPrice,
            Duration: duration,
            Type: itemType,
            Team: team,
            Enabled: itemTemplate.Enabled,
            CanBeSold: itemTemplate.CanBeSold
        );
        return true;
    }
    private static void NormalizeConfig(SmokeColorModuleConfig config)
    {
        config.Settings ??= new SmokeColorModuleSettings();
        config.Items ??= [];
    }

    private static bool TryGetColorVector(SmokeColorItemTemplate itemTemplate, out Vector color)
    {
        color = Vector.Zero;

        if (itemTemplate.Color is null || itemTemplate.Color.Count < 3)
        {
            return false;
        }

        var r = Math.Clamp(itemTemplate.Color[0], 0, 255);
        var g = Math.Clamp(itemTemplate.Color[1], 0, 255);
        var b = Math.Clamp(itemTemplate.Color[2], 0, 255);

        color = new Vector(r, g, b);
        return true;
    }

    private static SmokeColorModuleConfig CreateDefaultConfig()
    {
        return new SmokeColorModuleConfig
        {
            Settings = new SmokeColorModuleSettings
            {
                Category = DefaultCategory
            },
            Items =
            [
                new SmokeColorItemTemplate
                {
                    Id = "red_smoke_hourly",
                    DisplayNameKey = "module.smoke_color.item.temporary.name",
                    Price = 450,
                    SellPrice = 225,
                    DurationSeconds = 3600,
                    Type = nameof(ShopItemType.Temporary),
                    Team = nameof(ShopItemTeam.Any),
                    Enabled = true,
                    CanBeSold = true,
                    ColorName = "Red",
                    Color = [255, 0, 0]
                },
                new SmokeColorItemTemplate
                {
                    Id = "blue_smoke_hourly",
                    DisplayNameKey = "module.smoke_color.item.temporary.name",
                    Price = 450,
                    SellPrice = 225,
                    DurationSeconds = 3600,
                    Type = nameof(ShopItemType.Temporary),
                    Team = nameof(ShopItemTeam.Any),
                    Enabled = true,
                    CanBeSold = true,
                    ColorName = "Blue",
                    Color = [0, 120, 255]
                },
                new SmokeColorItemTemplate
                {
                    Id = "green_smoke_hourly",
                    DisplayNameKey = "module.smoke_color.item.temporary.name",
                    Price = 450,
                    SellPrice = 225,
                    DurationSeconds = 3600,
                    Type = nameof(ShopItemType.Temporary),
                    Team = nameof(ShopItemTeam.Any),
                    Enabled = true,
                    CanBeSold = true,
                    ColorName = "Green",
                    Color = [0, 200, 80]
                },
                new SmokeColorItemTemplate
                {
                    Id = "yellow_smoke_hourly",
                    DisplayNameKey = "module.smoke_color.item.temporary.name",
                    Price = 450,
                    SellPrice = 225,
                    DurationSeconds = 3600,
                    Type = nameof(ShopItemType.Temporary),
                    Team = nameof(ShopItemTeam.Any),
                    Enabled = true,
                    CanBeSold = true,
                    ColorName = "Yellow",
                    Color = [255, 215, 0]
                },
                new SmokeColorItemTemplate
                {
                    Id = "purple_smoke_hourly",
                    DisplayNameKey = "module.smoke_color.item.temporary.name",
                    Price = 450,
                    SellPrice = 225,
                    DurationSeconds = 3600,
                    Type = nameof(ShopItemType.Temporary),
                    Team = nameof(ShopItemTeam.Any),
                    Enabled = true,
                    CanBeSold = true,
                    ColorName = "Purple",
                    Color = [155, 89, 182]
                },
                new SmokeColorItemTemplate
                {
                    Id = "cyan_smoke_hourly",
                    DisplayNameKey = "module.smoke_color.item.temporary.name",
                    Price = 450,
                    SellPrice = 225,
                    DurationSeconds = 3600,
                    Type = nameof(ShopItemType.Temporary),
                    Team = nameof(ShopItemTeam.Any),
                    Enabled = true,
                    CanBeSold = true,
                    ColorName = "Cyan",
                    Color = [0, 255, 255]
                },
                new SmokeColorItemTemplate
                {
                    Id = "orange_smoke_hourly",
                    DisplayNameKey = "module.smoke_color.item.temporary.name",
                    Price = 450,
                    SellPrice = 225,
                    DurationSeconds = 3600,
                    Type = nameof(ShopItemType.Temporary),
                    Team = nameof(ShopItemTeam.Any),
                    Enabled = true,
                    CanBeSold = true,
                    ColorName = "Orange",
                    Color = [255, 140, 0]
                },
                new SmokeColorItemTemplate
                {
                    Id = "pink_smoke_hourly",
                    DisplayNameKey = "module.smoke_color.item.temporary.name",
                    Price = 450,
                    SellPrice = 225,
                    DurationSeconds = 3600,
                    Type = nameof(ShopItemType.Temporary),
                    Team = nameof(ShopItemTeam.Any),
                    Enabled = true,
                    CanBeSold = true,
                    ColorName = "Pink",
                    Color = [255, 105, 180]
                },
                new SmokeColorItemTemplate
                {
                    Id = "white_smoke_hourly",
                    DisplayNameKey = "module.smoke_color.item.temporary.name",
                    Price = 450,
                    SellPrice = 225,
                    DurationSeconds = 3600,
                    Type = nameof(ShopItemType.Temporary),
                    Team = nameof(ShopItemTeam.Any),
                    Enabled = true,
                    CanBeSold = true,
                    ColorName = "White",
                    Color = [255, 255, 255]
                }
            ]
        };
    }
    private static string ResolveDisplayName(ISwiftlyCore Core, SmokeColorItemTemplate item)
    {
        var key = item.DisplayNameKey?.Trim();

        if (string.IsNullOrWhiteSpace(key))
        {
            return $"{item.ColorName} Smoke";
        }

        if (item.Type.Equals("Permanent", StringComparison.OrdinalIgnoreCase))
        {
            return Core.Localizer[key, item.ColorName];
        }

        return Core.Localizer[key, item.ColorName, FormatDuration(item.DurationSeconds)];
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
}

internal sealed class SmokeColorModuleConfig
{
    public SmokeColorModuleSettings Settings { get; set; } = new();
    public List<SmokeColorItemTemplate> Items { get; set; } = new();
}
internal sealed class SmokeColorModuleSettings
{
    public string Category { get; set; } = "Visuals/Smoke Colors";
}
internal sealed class SmokeColorItemTemplate
{
    public string Id { get; set; } = string.Empty;
    public string DisplayNameKey { get; set; } = string.Empty;
    public int Price { get; set; } = 0;
    public int? SellPrice { get; set; }
    public int DurationSeconds { get; set; } = 0;
    public string Type { get; set; } = nameof(ShopItemType.Temporary);
    public string Team { get; set; } = nameof(ShopItemTeam.Any);
    public bool Enabled { get; set; } = true;
    public bool CanBeSold { get; set; } = true;
    public string ColorName { get; set; } = "Red";
    public List<int> Color { get; set; } = [];
}

internal readonly record struct SmokePreviewState(Vector Color, float ExpiresAt);

