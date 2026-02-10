using Microsoft.Extensions.Logging;
using ShopCore.Contract;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameEvents;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Plugins;
using SwiftlyS2.Shared.SchemaDefinitions;

namespace ShopCore;

[PluginMetadata(
    Id = "Shop_Healthshot",
    Name = "Shop Healthshot",
    Author = "T3Marius",
    Version = "1.0.0",
    Description = "ShopCore module with healthshot items."
)]
public class Shop_Healtshot : BasePlugin
{
    private const string ShopCoreInterfaceKey = "ShopCore.API.v1";
    private const string ModulePluginId = "Shop_Healthshot";
    private const string TemplateFileName = "items_config.jsonc";
    private const string TemplateSectionName = "Main";
    private const string DefaultCategory = "Healings";
    private const string DefaultWeaponDesignerName = "weapon_healthshot";

    private IShopCoreApiV1? shopApi;
    private bool handlersRegistered;
    private string healthshotDesignerName = DefaultWeaponDesignerName;
    private readonly HashSet<string> registeredItemIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> roundStartItemIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HealthshotGrantMode> itemGrantModes = new(StringComparer.OrdinalIgnoreCase);

    public Shop_Healtshot(ISwiftlyCore core) : base(core)
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
            Core.Logger.LogError(ex, "Failed to resolve shared interface '{InterfaceKey}'.", ShopCoreInterfaceKey);
        }
    }

    public override void OnSharedInterfaceInjected(IInterfaceManager interfaceManager)
    {
        if (shopApi is null)
        {
            Core.Logger.LogWarning("ShopCore API is not available. Healthshot test items will not be registered.");
            return;
        }

        RegisterItemsAndHandlers();
    }

    public override void Load(bool hotReload)
    {
    }

    public override void Unload()
    {
        UnregisterItemsAndHandlers();
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnRoundStart(EventRoundStart @event)
    {
        if (!handlersRegistered || shopApi is null)
        {
            return HookResult.Continue;
        }

        if (roundStartItemIds.Count == 0)
        {
            return HookResult.Continue;
        }

        // Delay slightly so player pawns/item services are ready.
        _ = Core.Scheduler.DelayBySeconds(0.25f, GiveRoundStartHealthshotsToAllPlayers);
        return HookResult.Continue;
    }

    private void RegisterItemsAndHandlers()
    {
        if (shopApi is null)
        {
            return;
        }

        UnregisterItemsAndHandlers();

        var moduleConfig = shopApi.LoadModuleTemplateConfig<HealthshotModuleConfig>(
            ModulePluginId,
            TemplateFileName,
            TemplateSectionName
        );
        NormalizeConfig(moduleConfig);

        var category = string.IsNullOrWhiteSpace(moduleConfig.Settings.Category)
            ? DefaultCategory
            : moduleConfig.Settings.Category.Trim();
        healthshotDesignerName = string.IsNullOrWhiteSpace(moduleConfig.Settings.WeaponDesignerName)
            ? DefaultWeaponDesignerName
            : moduleConfig.Settings.WeaponDesignerName.Trim();

        if (moduleConfig.Items.Count == 0)
        {
            moduleConfig = CreateDefaultConfig();
            category = moduleConfig.Settings.Category;
            healthshotDesignerName = moduleConfig.Settings.WeaponDesignerName;
            _ = shopApi.SaveModuleTemplateConfig(
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
            if (!TryCreateDefinition(itemTemplate, category, out var definition, out var grantMode))
            {
                continue;
            }

            if (!shopApi.RegisterItem(definition))
            {
                Core.Logger.LogWarning("Failed to register healthshot item '{ItemId}'.", definition.Id);
                continue;
            }

            _ = registeredItemIds.Add(definition.Id);
            itemGrantModes[definition.Id] = grantMode;

            if (grantMode == HealthshotGrantMode.RoundStartIfEquipped)
            {
                _ = roundStartItemIds.Add(definition.Id);
            }

            registeredCount++;
        }

        shopApi.OnItemPurchased += OnItemPurchased;
        handlersRegistered = true;

        Core.Logger.LogInformation(
            "Shop_Healthshot initialized. RegisteredItems={RegisteredItems}, RoundStartItems={RoundStartItems}, Weapon='{WeaponDesignerName}'",
            registeredCount,
            roundStartItemIds.Count,
            healthshotDesignerName
        );
    }

    private void UnregisterItemsAndHandlers()
    {
        if (!handlersRegistered || shopApi is null)
        {
            return;
        }

        shopApi.OnItemPurchased -= OnItemPurchased;
        foreach (var itemId in registeredItemIds)
        {
            _ = shopApi.UnregisterItem(itemId);
        }

        registeredItemIds.Clear();
        roundStartItemIds.Clear();
        itemGrantModes.Clear();
        healthshotDesignerName = DefaultWeaponDesignerName;
        handlersRegistered = false;
    }

    private void OnItemPurchased(IPlayer player, ShopItemDefinition item)
    {
        if (shopApi is null || !player.IsValid || player.IsFakeClient)
        {
            return;
        }

        if (!itemGrantModes.TryGetValue(item.Id, out var grantMode))
        {
            return;
        }

        if (grantMode != HealthshotGrantMode.OnPurchase)
        {
            return;
        }

        _ = GiveHealthshot(player);
    }

    private void GiveRoundStartHealthshotsToAllPlayers()
    {
        if (shopApi is null)
        {
            return;
        }

        foreach (var player in Core.PlayerManager.GetAllValidPlayers())
        {
            if (player.IsFakeClient || !player.IsAlive)
            {
                continue;
            }

            foreach (var itemId in roundStartItemIds)
            {
                if (!shopApi.IsItemEnabled(player, itemId))
                {
                    continue;
                }

                _ = GiveHealthshot(player);
            }
        }
    }

    private bool GiveHealthshot(IPlayer player)
    {
        if (!player.IsValid || !player.IsAlive)
        {
            return false;
        }

        Core.Scheduler.NextWorldUpdate(() =>
        {
            try
            {
                if (!player.IsValid || !player.IsAlive)
                {
                    return;
                }

                var itemServices = player.PlayerPawn?.ItemServices;
                if (itemServices is null || !itemServices.IsValid)
                {
                    return;
                }

                itemServices.GiveItem(healthshotDesignerName);
            }
            catch (Exception ex)
            {
                Core.Logger.LogWarning(ex, "Failed to give healthshot to player {PlayerId}.", player.PlayerID);
            }
        });

        return true;
    }

    private bool TryCreateDefinition(
        HealthshotItemTemplate itemTemplate,
        string category,
        out ShopItemDefinition definition,
        out HealthshotGrantMode grantMode)
    {
        definition = default!;
        grantMode = HealthshotGrantMode.OnPurchase;

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

        if (!Enum.TryParse(itemTemplate.Team, ignoreCase: true, out ShopItemTeam team))
        {
            team = ShopItemTeam.Any;
        }

        if (!Enum.TryParse(itemTemplate.GrantMode, ignoreCase: true, out grantMode))
        {
            grantMode = HealthshotGrantMode.OnPurchase;
        }

        if (grantMode == HealthshotGrantMode.RoundStartIfEquipped && itemType == ShopItemType.Consumable)
        {
            Core.Logger.LogWarning(
                "Skipping item '{ItemId}' because GrantMode '{GrantMode}' requires a non-consumable Type.",
                itemId,
                grantMode
            );
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
        return true;
    }

    private string ResolveDisplayName(HealthshotItemTemplate itemTemplate)
    {
        if (!string.IsNullOrWhiteSpace(itemTemplate.DisplayNameKey))
        {
            var key = itemTemplate.DisplayNameKey.Trim();
            var localized = Core.Localizer[key];
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

    private static void NormalizeConfig(HealthshotModuleConfig config)
    {
        config.Settings ??= new HealthshotModuleSettings();
        config.Items ??= [];
    }

    private static HealthshotModuleConfig CreateDefaultConfig()
    {
        return new HealthshotModuleConfig
        {
            Settings = new HealthshotModuleSettings
            {
                Category = DefaultCategory,
                WeaponDesignerName = DefaultWeaponDesignerName
            },
            Items =
            [
                new HealthshotItemTemplate
                {
                    Id = "healthshot_instant",
                    DisplayName = "Healthshot (Instant)",
                    DisplayNameKey = "module.healthshot.item.instant.name",
                    Price = 150,
                    SellPrice = null,
                    DurationSeconds = 0,
                    Type = nameof(ShopItemType.Consumable),
                    Team = nameof(ShopItemTeam.Any),
                    Enabled = true,
                    CanBeSold = false,
                    GrantMode = nameof(HealthshotGrantMode.OnPurchase)
                },
                new HealthshotItemTemplate
                {
                    Id = "healthshot_hourly",
                    DisplayName = "Healthshot (1 Hour)",
                    DisplayNameKey = "module.healthshot.item.hourly.name",
                    Price = 800,
                    SellPrice = 400,
                    DurationSeconds = 3600,
                    Type = nameof(ShopItemType.Temporary),
                    Team = nameof(ShopItemTeam.Any),
                    Enabled = true,
                    CanBeSold = true,
                    GrantMode = nameof(HealthshotGrantMode.RoundStartIfEquipped)
                }
            ]
        };
    }

}

internal enum HealthshotGrantMode
{
    OnPurchase = 0,
    RoundStartIfEquipped = 1
}

internal sealed class HealthshotModuleConfig
{
    public HealthshotModuleSettings Settings { get; set; } = new();
    public List<HealthshotItemTemplate> Items { get; set; } = [];
}

internal sealed class HealthshotModuleSettings
{
    public string Category { get; set; } = "Healings";
    public string WeaponDesignerName { get; set; } = "weapon_healthshot";
}

internal sealed class HealthshotItemTemplate
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string DisplayNameKey { get; set; } = string.Empty;
    public int Price { get; set; } = 0;
    public int? SellPrice { get; set; }
    public int DurationSeconds { get; set; } = 0;
    public string Type { get; set; } = nameof(ShopItemType.Consumable);
    public string Team { get; set; } = nameof(ShopItemTeam.Any);
    public bool Enabled { get; set; } = true;
    public bool CanBeSold { get; set; } = true;
    public string GrantMode { get; set; } = nameof(HealthshotGrantMode.OnPurchase);
}
