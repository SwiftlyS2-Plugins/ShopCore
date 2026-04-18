using SwiftlyS2.Shared.Plugins;
using ShopCore.Contract;
using SwiftlyS2.Shared;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.GameEvents;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.SchemaDefinitions;

namespace ShopCore;

[PluginMetadata(
    Id = "Shop_Killscreen",
    Name = "Shop Killscreen",
    Author = "T3Marius",
    Version = "1.0.0",
    Description = "ShopCore module with killscreen items"
)]
public class Shop_Killscreen : BasePlugin
{
    private const string ShopCoreInterfaceKey = "ShopCore.API.v2";
    private const string ModulePluginId = "Shop_Killscreen";
    private const string TemplateFileName = "killscreen_config.jsonc";
    private const string TemplateSectionName = "Main";
    private const string DefaultCategory = "Visuals/Kill Screens";

    private IShopCoreApiV2? shopApi;
    private bool handlersRegistered;
    private readonly HashSet<string> registeredItemIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> registeredItemOrder = new();
    private KillscreenModuleSettings runtimeSettings = new();

    public Shop_Killscreen(ISwiftlyCore core) : base(core) { }
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
            Core.Logger.LogWarning("ShopCore API is not available. Killscreen items will not be registered.");
            return;
        }

        RegisterItemsAndHandlers();
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
    public HookResult OnPlayerDeath(EventPlayerDeath e)
    {
        IPlayer? attacker = e.AttackerPlayer;
        if (attacker == null || attacker.IsFakeClient || !attacker.IsValid)
            return HookResult.Continue;

        CCSPlayerPawn? pawn = attacker.PlayerPawn;
        if (pawn == null || !pawn.IsValid)
            return HookResult.Continue;

        if (shopApi == null)
            return HookResult.Continue;

        foreach (var itemId in registeredItemOrder)
        {
            if (!shopApi.IsItemEnabled(attacker, itemId))
                continue;

            var currentTime = Core.Engine.GlobalVars.CurrentTime;
            pawn.HealthShotBoostExpirationTime.Value = currentTime + 1.0f;
            pawn.HealthShotBoostExpirationTimeUpdated();
        }

        return HookResult.Continue;
    }

    private void RegisterItemsAndHandlers()
    {
        if (shopApi == null)
        {
            return;
        }

        UnregisterItemsAndHandlers();

        var moduleConfig = shopApi.LoadModuleConfig<KillscreenModuleConfig>(
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
            if (!TryCreateDefinition(itemTemplate, category, out var definition))
            {
                continue;
            }

            if (!shopApi.RegisterItem(definition))
            {
                Core.Logger.LogWarning("Failed to register killscreen item '{ItemId}'.", definition.Id);
                continue;
            }

            _ = registeredItemIds.Add(definition.Id);
            registeredItemOrder.Add(definition.Id);
            registeredCount++;
        }

        shopApi.OnItemToggled += OnItemToggled;
        shopApi.OnItemPreview += OnItemPreview;
        handlersRegistered = true;

        Core.Logger.LogInformation(
            "Shop_Killscreen initialized. RegisteredItems={RegisteredItems}",
            registeredCount
        );
    }
    private void UnregisterItemsAndHandlers()
    {
        if (!handlersRegistered || shopApi == null)
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
        handlersRegistered = false;
    }
    private void OnItemToggled(IPlayer player, ShopItemDefinition item, bool enabled)
    {
        if (!enabled || shopApi == null || !registeredItemIds.Contains(item.Id))
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

        Core.Scheduler.NextWorldUpdate(() =>
        {
            if (!player.IsValid || player.IsFakeClient)
            {
                return;
            }

            var pawn = player.PlayerPawn;
            if (pawn == null || !pawn.IsValid)
            {
                return;
            }

            var currentTime = Core.Engine.GlobalVars.CurrentTime;
            pawn.HealthShotBoostExpirationTime.Value = currentTime + 1.0f;
            pawn.HealthShotBoostExpirationTimeUpdated();
            player.SendChat($"{GetPrefix(player)} {Core.Translation.GetPlayerLocalizer(player)["preview.started", shopApi?.GetItemDisplayName(player, item) ?? item.DisplayName]}");
        });
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
    private bool TryCreateDefinition(
        KillscreenItemTemplate itemTemplate,
        string category,
        out ShopItemDefinition definition
        )
    {
        definition = default!;

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
                "Skipping item '{ItemId}' because killscreen items cannot use Type '{Type}'.",
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
            CanBeSold: itemTemplate.CanBeSold,
            DisplayNameResolver: player => ResolveDisplayName(Core, itemTemplate, player)
        );
        return true;
    }
    private static void NormalizeConfig(KillscreenModuleConfig config)
    {
        config.Settings ??= new KillscreenModuleSettings();
        config.Items ??= [];
    }
    private KillscreenModuleConfig CreateDefaultConfig()
    {
        return new KillscreenModuleConfig
        {
            Settings = new KillscreenModuleSettings
            {
                Category = DefaultCategory
            },
            Items =
            [
                new KillscreenItemTemplate
                {
                    Id = "killscreen_hourly",
                    DisplayNameKey = "item.temporary.name",
                    Price = 1000,
                    SellPrice = 500,
                    DurationSeconds = 3600,
                    Type = nameof(ShopItemType.Temporary),
                    Team = nameof(ShopItemTeam.Any),
                    Enabled = true,
                    CanBeSold = true
                },
                new KillscreenItemTemplate
                {
                    Id = "killscreen_perm",
                    DisplayNameKey = "item.permanent.name",
                    Price = 5000,
                    SellPrice = 500,
                    DurationSeconds = 0,
                    Type = nameof(ShopItemType.Permanent),
                    Team = nameof(ShopItemTeam.Any),
                    Enabled = true,
                    CanBeSold = true
                }
            ]
        };
    }
    private static string ResolveDisplayName(ISwiftlyCore Core, KillscreenItemTemplate item, IPlayer? player = null)
    {
        var key = item.DisplayNameKey?.Trim();
        var localizer = player == null ? Core.Localizer : Core.Translation.GetPlayerLocalizer(player);

        if (string.IsNullOrWhiteSpace(key))
        {
            return $"Killscreen";
        }

        if (item.Type.Equals("Permanent", StringComparison.OrdinalIgnoreCase))
        {
            return localizer[key];
        }

        return localizer[key, FormatDuration(item.DurationSeconds)];
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
internal sealed class KillscreenModuleConfig
{
    public KillscreenModuleSettings Settings { get; set; } = new();
    public List<KillscreenItemTemplate> Items { get; set; } = new();
}
internal sealed class KillscreenModuleSettings
{
    public bool UseCorePrefix { get; set; } = true;
    public string Category { get; set; } = "Visuals/Kill Screens";
}
internal sealed class KillscreenItemTemplate
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
}
