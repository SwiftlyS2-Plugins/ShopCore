using Microsoft.Extensions.Logging;
using ShopCore.Contract;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameEvents;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Plugins;

namespace ShopCore;

[PluginMetadata(
    Id = "Shop_HitSounds",
    Name = "Shop HitSounds",
    Author = "T3Marius",
    Version = "1.0.0",
    Description = "ShopCore module with hit-sound items"
)]
public class Shop_HitSounds : BasePlugin
{
    private const string ShopCoreInterfaceKey = "ShopCore.API.v1";
    private const string ModulePluginId = "Shop_HitSounds";
    private const string TemplateFileName = "items_config.jsonc";
    private const string TemplateSectionName = "Main";
    private const string DefaultCategory = "Sounds/Hit Sounds";

    private IShopCoreApiV1? shopApi;
    private bool handlersRegistered;
    private readonly HashSet<string> registeredItemIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> registeredItemOrder = new();
    private readonly Dictionary<string, HitSoundItemRuntime> itemRuntimeById = new(StringComparer.OrdinalIgnoreCase);

    public Shop_HitSounds(ISwiftlyCore core) : base(core) { }

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
            Core.Logger.LogWarning("ShopCore API is not available. HitSounds items will not be registered.");
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
    public HookResult OnPlayerHurt(EventPlayerHurt e)
    {
        if (!handlersRegistered || shopApi is null)
        {
            return HookResult.Continue;
        }

        var attacker = e.AttackerPlayer;
        if (attacker is null || !attacker.IsValid || attacker.IsFakeClient)
        {
            return HookResult.Continue;
        }

        if (!TryGetEnabledSound(attacker, out var soundPath))
        {
            return HookResult.Continue;
        }

        PlayHitSound(attacker, soundPath);
        return HookResult.Continue;
    }

    private void RegisterItemsAndHandlers()
    {
        if (shopApi is null)
        {
            return;
        }

        UnregisterItemsAndHandlers();

        var moduleConfig = shopApi.LoadModuleTemplateConfig<HitSoundsModuleConfig>(
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
            if (!TryCreateDefinition(itemTemplate, category, out var definition, out var runtime))
            {
                continue;
            }

            if (!shopApi.RegisterItem(definition))
            {
                Core.Logger.LogWarning("Failed to register hit-sound item '{ItemId}'.", definition.Id);
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
            "Shop_HitSounds initialized. RegisteredItems={RegisteredItems}",
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
            "module.hitsounds.error.permission",
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

    private void OnItemSold(IPlayer player, ShopItemDefinition item, decimal creditedAmount)
    {
        if (!registeredItemIds.Contains(item.Id) || shopApi is null)
        {
            return;
        }

        // If sold while equipped, make sure any other enabled item becomes active source.
        foreach (var otherItemId in registeredItemOrder)
        {
            if (shopApi.IsItemEnabled(player, otherItemId))
            {
                return;
            }
        }
    }

    private void OnItemExpired(IPlayer player, ShopItemDefinition item)
    {
        if (!registeredItemIds.Contains(item.Id) || shopApi is null)
        {
            return;
        }

        foreach (var otherItemId in registeredItemOrder)
        {
            if (shopApi.IsItemEnabled(player, otherItemId))
            {
                return;
            }
        }
    }

    private bool TryGetEnabledSound(IPlayer player, out string soundPath)
    {
        soundPath = string.Empty;

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

            if (!itemRuntimeById.TryGetValue(itemId, out var runtime))
            {
                continue;
            }

            soundPath = runtime.SoundPath;
            return !string.IsNullOrWhiteSpace(soundPath);
        }

        return false;
    }

    private void PlayHitSound(IPlayer player, string soundPath)
    {
        if (string.IsNullOrWhiteSpace(soundPath))
        {
            return;
        }

        RunOnMainThread(() =>
        {
            if (!player.IsValid || player.IsFakeClient)
            {
                return;
            }

            player.ExecuteCommand($"play {soundPath}");
        });
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
                Core.Logger.LogWarning(ex, "Shop_HitSounds main-thread action failed.");
            }
        });
    }

    private bool TryCreateDefinition(
        HitSoundItemTemplate itemTemplate,
        string category,
        out ShopItemDefinition definition,
        out HitSoundItemRuntime runtime)
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

        if (string.IsNullOrWhiteSpace(itemTemplate.SoundPath))
        {
            Core.Logger.LogWarning("Skipping item '{ItemId}' because SoundPath is empty.", itemId);
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
                "Skipping item '{ItemId}' because hit-sound items cannot use Type '{Type}'.",
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

        runtime = new HitSoundItemRuntime(
            ItemId: itemId,
            SoundPath: itemTemplate.SoundPath.Trim(),
            RequiredPermission: itemTemplate.RequiredPermission?.Trim() ?? string.Empty
        );

        return true;
    }

    private string ResolveDisplayName(HitSoundItemTemplate itemTemplate)
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

    private static void NormalizeConfig(HitSoundsModuleConfig config)
    {
        config.Settings ??= new HitSoundsModuleSettings();
        config.Items ??= [];
    }

    private static HitSoundsModuleConfig CreateDefaultConfig()
    {
        return new HitSoundsModuleConfig
        {
            Settings = new HitSoundsModuleSettings
            {
                Category = DefaultCategory
            },
            Items =
            [
                new HitSoundItemTemplate
                {
                    Id = "hitsound_bell_hourly",
                    DisplayNameKey = "module.hitsounds.item.bell.name",
                    SoundPath = "sounds/training/bell_normal.vsnd_c",
                    Price = 1200,
                    SellPrice = 600,
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

internal readonly record struct HitSoundItemRuntime(
    string ItemId,
    string SoundPath,
    string RequiredPermission
);

internal sealed class HitSoundsModuleConfig
{
    public HitSoundsModuleSettings Settings { get; set; } = new();
    public List<HitSoundItemTemplate> Items { get; set; } = [];
}

internal sealed class HitSoundsModuleSettings
{
    public string Category { get; set; } = "Visuals/Hit Sounds";
}

internal sealed class HitSoundItemTemplate
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string DisplayNameKey { get; set; } = string.Empty;
    public string SoundPath { get; set; } = string.Empty;
    public int Price { get; set; } = 0;
    public int? SellPrice { get; set; }
    public int DurationSeconds { get; set; } = 0;
    public string Type { get; set; } = nameof(ShopItemType.Temporary);
    public string Team { get; set; } = nameof(ShopItemTeam.Any);
    public bool Enabled { get; set; } = true;
    public bool CanBeSold { get; set; } = true;
    public string RequiredPermission { get; set; } = string.Empty;
}
