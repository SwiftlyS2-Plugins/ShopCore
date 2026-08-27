using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using ShopCore.Contract;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Plugins;
using SwiftlyS2.Shared.Translation;
using VIPCore.Contract;

namespace ShopCore;

[PluginMetadata(
    Id = "Shop_VIP",
    Version = "1.0.0",
    Name = "Shop VIP",
    Author = "aga",
    Description = "Allows purchasing VIPCore groups from ShopCore"
)]
public partial class Shop_VIP : BasePlugin
{
    private const string ShopCoreInterfaceKey = "ShopCore.API.v2";
    private const string VipCoreInterfaceKey = "VIPCore.Api.v1";
    private const string ModulePluginId = "Shop_VIP";
    private const string ConfigFileName = "vip_items.jsonc";
    private const string ConfigSectionName = "Main";
    private const string DefaultCategory = "VIP/Memberships";

    private IShopCoreApiV2? shopApi;
    private IVipCoreApiV1? vipApi;

    private bool handlersRegistered;
    private VipShopModuleSettings runtimeSettings = new();

    private readonly HashSet<string> registeredItemIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VipShopItemRuntime> runtimeByItemId = new(StringComparer.OrdinalIgnoreCase);

    public Shop_VIP(ISwiftlyCore core) : base(core)
    {
    }

    public override void UseSharedInterface(IInterfaceManager interfaceManager)
    {
        shopApi = ResolveSharedInterface<IShopCoreApiV2>(interfaceManager, ShopCoreInterfaceKey);
        vipApi = ResolveSharedInterface<IVipCoreApiV1>(interfaceManager, VipCoreInterfaceKey);
    }

    public override void OnSharedInterfaceInjected(IInterfaceManager interfaceManager)
    {
        if (shopApi is null)
        {
            shopApi = ResolveSharedInterface<IShopCoreApiV2>(interfaceManager, ShopCoreInterfaceKey);
        }

        if (vipApi is null)
        {
            vipApi = ResolveSharedInterface<IVipCoreApiV1>(interfaceManager, VipCoreInterfaceKey);
        }

        if (!handlersRegistered)
        {
            RegisterItemsAndHandlers();
        }
    }

    public override void Load(bool hotReload)
    {
        if (shopApi is not null && vipApi is not null && !handlersRegistered)
        {
            RegisterItemsAndHandlers();
        }
    }

    public override void Unload()
    {
        UnregisterItemsAndHandlers();
    }

    private TInterface? ResolveSharedInterface<TInterface>(IInterfaceManager interfaceManager, string key)
        where TInterface : class
    {
        try
        {
            if (!interfaceManager.HasSharedInterface(key))
            {
                return default;
            }

            return interfaceManager.GetSharedInterface<TInterface>(key);
        }
        catch (Exception ex)
        {
            Core.Logger.LogInformation(ex, "Failed to resolve shared interface '{InterfaceKey}'.", key);
            return default;
        }
    }

    private void RegisterItemsAndHandlers()
    {
        if (shopApi is null)
        {
            Core.Logger.LogWarning("ShopCore API is not available. VIP items cannot be registered.");
            return;
        }

        if (vipApi is null)
        {
            Core.Logger.LogWarning("VIPCore API is not available. VIP items cannot be registered yet.");
            return;
        }

        UnregisterItemsAndHandlers();

        var moduleConfig = shopApi.LoadModuleConfig<VipShopModuleConfig>(
            ModulePluginId,
            ConfigFileName,
            ConfigSectionName
        );

        NormalizeConfig(moduleConfig);
        runtimeSettings = moduleConfig.Settings;

        var category = string.IsNullOrWhiteSpace(runtimeSettings.Category)
            ? DefaultCategory
            : runtimeSettings.Category.Trim();

        if (moduleConfig.Items.Count == 0)
        {
            moduleConfig = CreateDefaultConfig();
            runtimeSettings = moduleConfig.Settings;
            category = runtimeSettings.Category;

            _ = shopApi.SaveModuleConfig(
                ModulePluginId,
                moduleConfig,
                ConfigFileName,
                ConfigSectionName,
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
                Core.Logger.LogWarning("Failed to register VIP item '{ItemId}'.", definition.Id);
                continue;
            }

            _ = registeredItemIds.Add(definition.Id);
            runtimeByItemId[definition.Id] = runtime;
            registeredCount++;
        }

        shopApi.OnBeforeItemPurchase += OnBeforeItemPurchase;
        shopApi.OnItemPurchased += OnItemPurchased;
        shopApi.OnItemSold += OnItemSold;
        shopApi.OnItemExpired += OnItemExpired;
        handlersRegistered = true;

        Core.Logger.LogInformation(
            "Shop_VIP initialized. RegisteredItems={RegisteredItems}",
            registeredCount
        );
    }

    private void UnregisterItemsAndHandlers()
    {
        if (!handlersRegistered || shopApi is null)
        {
            registeredItemIds.Clear();
            runtimeByItemId.Clear();
            handlersRegistered = false;
            return;
        }

        shopApi.OnBeforeItemPurchase -= OnBeforeItemPurchase;
        shopApi.OnItemPurchased -= OnItemPurchased;
        shopApi.OnItemSold -= OnItemSold;
        shopApi.OnItemExpired -= OnItemExpired;

        foreach (var itemId in registeredItemIds)
        {
            _ = shopApi.UnregisterItem(itemId);
        }

        registeredItemIds.Clear();
        runtimeByItemId.Clear();
        handlersRegistered = false;
    }

    private void OnBeforeItemPurchase(ShopBeforePurchaseContext context)
    {
        if (!TryGetRuntime(context.Item.Id, out var runtime))
        {
            return;
        }

        if (vipApi is null)
        {
            context.Block($"{GetPrefix(context.Player)} VIP system is not available right now.");
            return;
        }

        if (!runtime.BlockWhenHasEqualOrHigherGroup)
        {
            return;
        }

        var currentGroup = vipApi.GetClientVipGroup(context.Player);
        if (!string.IsNullOrWhiteSpace(currentGroup) && currentGroup.Equals(runtime.TargetGroup, StringComparison.OrdinalIgnoreCase))
        {
            context.Block($"{GetPrefix(context.Player)} You already have {runtime.TargetGroup} VIP access.");
        }
    }

    private void OnItemPurchased(IPlayer player, ShopItemDefinition item)
    {
        if (!TryGetRuntime(item.Id, out var runtime) || vipApi is null)
        {
            return;
        }

        var capturedRuntime = runtime;
        var capturedVipApi = vipApi;
        Core.Scheduler.NextWorldUpdate(() =>
        {
            capturedVipApi.GiveClientVip(player, capturedRuntime.TargetGroup, capturedRuntime.VipTime);
            var loc = Core.Translation.GetPlayerLocalizer(player);
            player.SendMessage(MessageType.Chat, $"{GetPrefix(player)} {loc["shop.vip.granted", capturedRuntime.TargetGroup]}");
        });
    }

    private void OnItemSold(IPlayer player, ShopItemDefinition item, decimal _)
    {
        HandleItemRemoval(player, item);
    }

    private void OnItemExpired(IPlayer player, ShopItemDefinition item)
    {
        HandleItemRemoval(player, item);
    }

    private void HandleItemRemoval(IPlayer player, ShopItemDefinition item)
    {
        if (!TryGetRuntime(item.Id, out var runtime) || vipApi is null)
        {
            return;
        }

        if (runtime.GrantBehavior == VipGrantBehavior.TemporaryGrant)
        {
            var capturedVipApi = vipApi;
            Core.Scheduler.NextWorldUpdate(() =>
            {
                capturedVipApi.RemoveClientVip(player);
                var loc = Core.Translation.GetPlayerLocalizer(player);
                player.SendMessage(MessageType.Chat, $"{GetPrefix(player)} {loc["shop.vip.removed"]}");
            });
        }
    }

    private bool TryGetRuntime(string itemId, out VipShopItemRuntime runtime)
    {
        if (runtimeByItemId.TryGetValue(itemId, out var found))
        {
            runtime = found;
            return true;
        }

        runtime = default!;
        return false;
    }

    private static void NormalizeConfig(VipShopModuleConfig config)
    {
        if (config.Items.Count <= 1)
        {
            return;
        }

        var unique = new Dictionary<string, VipShopItemTemplate>(StringComparer.OrdinalIgnoreCase);
        foreach (var template in config.Items)
        {
            if (string.IsNullOrWhiteSpace(template.Id))
            {
                continue;
            }

            var key = template.Id.Trim();
            unique[key] = template;
        }

        config.Items = unique.Values.ToList();
    }

    private bool TryCreateDefinition(
        VipShopItemTemplate template,
        string category,
        out ShopItemDefinition definition,
        out VipShopItemRuntime runtime)
    {
        definition = default!;
        runtime = default!;

        if (string.IsNullOrWhiteSpace(template.Id))
        {
            Core.Logger.LogWarning("[Shop_VIP] Skipping item with empty id.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(template.Group))
        {
            Core.Logger.LogWarning("[Shop_VIP] Item '{ItemId}' is missing target VIP group.", template.Id);
            return false;
        }

        var id = template.Id.Trim();
        var group = template.Group.Trim();
        var displayName = string.IsNullOrWhiteSpace(template.DisplayName)
            ? group
            : template.DisplayName.Trim();

        var price = template.Price < 0 ? 0 : template.Price;
        var duration = template.ShopDurationHours.HasValue && template.ShopDurationHours > 0
            ? TimeSpan.FromHours(template.ShopDurationHours.Value)
            : (TimeSpan?)null;

        var itemType = template.IsEquipable ? ShopItemType.Permanent : ShopItemType.Temporary;

        definition = new ShopItemDefinition(
            id,
            displayName,
            category,
            price,
            template.SellPrice,
            duration,
            itemType,
            ShopItemTeam.Any,
            template.Enabled,
            template.CanBeSold,
            template.AllowPreview,
            template.IsEquipable
        );

        var vipTime = 0;
        if (template.GrantBehavior == VipGrantBehavior.PermanentRecord && template.ShopDurationHours.HasValue && template.ShopDurationHours > 0)
        {
            var hours = template.ShopDurationHours.Value;
            vipTime = runtimeSettings.VipTimeMode switch
            {
                1 => (int)(hours * 60),
                2 => (int)hours,
                3 => (int)(hours / 24),
                _ => (int)(hours * 3600)
            };
        }

        runtime = new VipShopItemRuntime(
            group,
            template.GrantBehavior,
            vipTime,
            template.BlockWhenHasEqualOrHigherGroup
        );

        return true;
    }

    private VipShopModuleConfig CreateDefaultConfig()
    {
        return new VipShopModuleConfig
        {
            Settings = new VipShopModuleSettings
            {
                Category = DefaultCategory,
                AllowPurchaseWithoutVipCore = false,
                VipTimeMode = 0
            },
            Items =
            [
                new VipShopItemTemplate
                {
                    Id = "vip.bronze.7d",
                    DisplayName = "Bronze VIP (7 days)",
                    Group = "Bronze",
                    Price = 7500,
                    ShopDurationHours = 24 * 7,
                    GrantBehavior = VipGrantBehavior.PermanentRecord,
                    IsEquipable = false,
                    AllowPreview = false,
                    CanBeSold = false
                },
                new VipShopItemTemplate
                {
                    Id = "vip.bronze.session",
                    DisplayName = "Bronze VIP (session)",
                    Group = "Bronze",
                    Price = 1200,
                    ShopDurationHours = 6,
                    GrantBehavior = VipGrantBehavior.TemporaryGrant,
                    IsEquipable = false,
                    AllowPreview = false,
                    CanBeSold = false,
                    BlockWhenHasEqualOrHigherGroup = true
                }
            ]
        };
    }

    private string GetPrefix(IPlayer player)
    {
        var prefix = shopApi?.GetShopPrefix(player);
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            return prefix;
        }

        ILocalizer localizer;
        try
        {
            localizer = Core.Translation.GetPlayerLocalizer(player);
        }
        catch
        {
            return "[SHOP]";
        }

        return localizer["shop.prefix"];
    }

    private sealed record VipShopItemRuntime(
        string TargetGroup,
        VipGrantBehavior GrantBehavior,
        int VipTime,
        bool BlockWhenHasEqualOrHigherGroup
    );

    internal enum VipGrantBehavior
    {
        PermanentRecord,
        TemporaryGrant
    }

    internal sealed class VipShopModuleConfig
    {
        public VipShopModuleSettings Settings { get; set; } = new();
        public List<VipShopItemTemplate> Items { get; set; } = [];
    }

    internal sealed class VipShopModuleSettings
    {
        public string Category { get; set; } = string.Empty;
        public bool AllowPurchaseWithoutVipCore { get; set; }
        // Must match VIPCore's TimeMode: 0 = seconds, 1 = minutes, 2 = hours, 3 = days
        public int VipTimeMode { get; set; } = 0;
    }

    internal sealed class VipShopItemTemplate
    {
        public string Id { get; set; } = string.Empty;
        public string? DisplayName { get; set; }
        public string Group { get; set; } = string.Empty;
        public decimal Price { get; set; }
        public decimal? SellPrice { get; set; }
        public double? ShopDurationHours { get; set; }
        public VipGrantBehavior GrantBehavior { get; set; } = VipGrantBehavior.TemporaryGrant;
        public bool BlockWhenHasEqualOrHigherGroup { get; set; } = true;
        public bool Enabled { get; set; } = true;
        public bool CanBeSold { get; set; } = false;
        public bool AllowPreview { get; set; } = false;
        public bool IsEquipable { get; set; } = false;
    }
}