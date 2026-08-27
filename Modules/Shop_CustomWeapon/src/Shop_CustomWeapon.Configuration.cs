using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ShopCore.Contract;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Players;

namespace ShopCore;

public sealed partial class Shop_CustomWeapon
{
    private void LoadEarlyPrecacheModels()
    {
        earlyPrecacheModels.Clear();

        try
        {
            var configPath = Path.Combine(
                Core.CSGODirectory,
                "addons", "swiftlys2", "configs", "plugins",
                "ShopCore", "modules", ConfigFileName
            );

            Core.Logger.LogInformation("[Shop_CustomWeapon] Looking for config at: {Path}", configPath);

            if (!File.Exists(configPath))
            {
                Core.Logger.LogWarning("[Shop_CustomWeapon] Config file not found at: {Path}", configPath);
                return;
            }

            var rawText = File.ReadAllText(configPath);
            using var document = JsonDocument.Parse(rawText, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            var root = document.RootElement;
            if (root.TryGetProperty(ConfigSectionName, out var section))
            {
                root = section;
            }

            if (!root.TryGetProperty("Items", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                Core.Logger.LogWarning("[Shop_CustomWeapon] No 'Items' array found in config.");
                return;
            }

            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("PrecacheModel", out var precacheModel) &&
                    precacheModel.ValueKind == JsonValueKind.String)
                {
                    var modelPath = precacheModel.GetString();
                    if (!string.IsNullOrWhiteSpace(modelPath))
                    {
                        earlyPrecacheModels.Add(modelPath);
                    }
                }

                if (item.TryGetProperty("Weapon", out var weapon) &&
                    weapon.ValueKind == JsonValueKind.String)
                {
                    var weaponSpec = weapon.GetString() ?? string.Empty;
                    var separator = weaponSpec.Contains('|') ? '|' : ':';
                    var parts = weaponSpec.Split(separator, 2);
                    if (parts.Length > 1 && parts[1].Trim().EndsWith(".vmdl", StringComparison.OrdinalIgnoreCase))
                    {
                        earlyPrecacheModels.Add(parts[1].Trim());
                    }
                }
            }

            Core.Logger.LogInformation(
                "[Shop_CustomWeapon] Loaded {Count} model(s) for early precache from config.",
                earlyPrecacheModels.Count
            );
        }
        catch (Exception ex)
        {
            Core.Logger.LogWarning(ex, "[Shop_CustomWeapon] Failed to load config for early precache.");
        }
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
            Core.Logger.LogWarning("ShopCore API is not available. Custom weapon items cannot be registered.");
            return;
        }

        UnregisterItemsAndHandlers();

        var moduleConfig = shopApi.LoadModuleConfig<CustomWeaponModuleConfig>(
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
                Core.Logger.LogWarning("Failed to register custom weapon item '{ItemId}'.", definition.Id);
                continue;
            }

            _ = registeredItemIds.Add(definition.Id);
            runtimeByItemId[definition.Id] = runtime;
            registeredCount++;
        }

        shopApi.OnItemPurchased += OnItemPurchased;
        shopApi.OnItemToggled += OnItemToggled;
        shopApi.OnItemSold += OnItemSold;
        shopApi.OnItemExpired += OnItemExpired;
        shopApi.OnItemPreview += OnItemPreview;
        handlersRegistered = true;

        Core.Logger.LogInformation(
            "Shop_CustomWeapon initialized. RegisteredItems={RegisteredItems}",
            registeredCount
        );
    }

    private void UnregisterItemsAndHandlers()
    {
        if (shopApi is not null && handlersRegistered)
        {
            shopApi.OnItemPurchased -= OnItemPurchased;
            shopApi.OnItemToggled -= OnItemToggled;
            shopApi.OnItemSold -= OnItemSold;
            shopApi.OnItemExpired -= OnItemExpired;
            shopApi.OnItemPreview -= OnItemPreview;

            foreach (var itemId in registeredItemIds)
            {
                _ = shopApi.UnregisterItem(itemId);
            }
        }

        registeredItemIds.Clear();
        runtimeByItemId.Clear();
        originalSubclassByWeaponAddress.Clear();
        originalModelByWeaponAddress.Clear();
        originalNameByWeaponAddress.Clear();
        handlersRegistered = false;
    }

    private bool TryGetRuntime(string itemId, out CustomWeaponRuntime runtime)
    {
        if (runtimeByItemId.TryGetValue(itemId, out var found))
        {
            runtime = found;
            return true;
        }

        runtime = default!;
        return false;
    }

    private bool TryCreateDefinition(
        CustomWeaponItemTemplate template,
        string category,
        out ShopItemDefinition definition,
        out CustomWeaponRuntime runtime)
    {
        definition = default!;
        runtime = default!;

        if (string.IsNullOrWhiteSpace(template.Id))
        {
            Core.Logger.LogWarning("[Shop_CustomWeapon] Skipping item with empty id.");
            return false;
        }

        var baseWeapon = string.Empty;
        var vdataName = string.Empty;
        var precacheModel = string.Empty;

        if (!string.IsNullOrWhiteSpace(template.Weapon))
        {
            if (!CustomWeaponParsing.TryParseWeaponSpec(template.Weapon, out baseWeapon, out var appearanceValue, out var usesModelPath))
            {
                Core.Logger.LogWarning("[Shop_CustomWeapon] Item '{ItemId}' has invalid weapon specification '{Weapon}'.", template.Id, template.Weapon);
                return false;
            }

            if (usesModelPath)
            {
                precacheModel = appearanceValue;
            }
            else
            {
                vdataName = appearanceValue;
            }
        }

        if (!string.IsNullOrWhiteSpace(template.BaseWeapon))
        {
            baseWeapon = template.BaseWeapon.Trim();
        }

        if (!string.IsNullOrWhiteSpace(template.VdataName))
        {
            vdataName = template.VdataName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(template.PrecacheModel))
        {
            precacheModel = template.PrecacheModel.Trim();
        }

        if (string.IsNullOrWhiteSpace(baseWeapon) || (string.IsNullOrWhiteSpace(vdataName) && string.IsNullOrWhiteSpace(precacheModel)))
        {
            Core.Logger.LogWarning("[Shop_CustomWeapon] Item '{ItemId}' is missing BaseWeapon and/or appearance data.", template.Id);
            return false;
        }

        if (!Enum.TryParse(template.Type, ignoreCase: true, out ShopItemType itemType))
        {
            itemType = ShopItemType.Temporary;
        }

        if (!Enum.TryParse(template.Team, ignoreCase: true, out ShopItemTeam team))
        {
            team = ShopItemTeam.Any;
        }

        var itemId = template.Id.Trim();
        var displayName = string.IsNullOrWhiteSpace(template.DisplayName)
            ? itemId
            : template.DisplayName.Trim();
        var duration = template.DurationSeconds > 0
            ? TimeSpan.FromSeconds(template.DurationSeconds)
            : (TimeSpan?)null;
        decimal? sellPrice = template.SellPrice.HasValue && template.SellPrice.Value >= 0
            ? template.SellPrice.Value
            : null;
        var resolvedCategory = CustomWeaponParsing.ResolveItemCategory(category, template, baseWeapon, DefaultCategory);

        definition = new ShopItemDefinition(
            Id: itemId,
            DisplayName: displayName,
            Category: resolvedCategory,
            Price: template.Price < 0 ? 0 : template.Price,
            SellPrice: sellPrice,
            Duration: duration,
            Type: itemType,
            Team: team,
            Enabled: template.Enabled,
            CanBeSold: template.CanBeSold,
            AllowPreview: template.AllowPreview,
            IsEquipable: template.IsEquipable
        );

        runtime = new CustomWeaponRuntime(
            itemId,
            displayName,
            baseWeapon,
            vdataName,
            precacheModel,
            template.GrantOnPurchase,
            template.GrantOnRoundStart,
            template.SelectWeaponOnEquip
        );

        return true;
    }

    private string GetPrefix(IPlayer player)
    {
        if (runtimeSettings.UseCorePrefix)
        {
            var prefix = shopApi?.GetShopPrefix(player);
            if (!string.IsNullOrWhiteSpace(prefix))
            {
                return prefix;
            }
        }

        return Core.Translation.GetPlayerLocalizer(player)["shop.prefix"];
    }

    private static void NormalizeConfig(CustomWeaponModuleConfig config)
    {
        config.Settings ??= new CustomWeaponModuleSettings();
        config.Items ??= [];

        if (config.Items.Count <= 1)
        {
            return;
        }

        var unique = new Dictionary<string, CustomWeaponItemTemplate>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in config.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Id))
            {
                continue;
            }

            unique[item.Id.Trim()] = item;
        }

        config.Items = unique.Values.ToList();
    }

    private static CustomWeaponModuleConfig CreateDefaultConfig()
    {
        return new CustomWeaponModuleConfig
        {
            Settings = new CustomWeaponModuleSettings
            {
                UseCorePrefix = true,
                Category = DefaultCategory
            },
            Items =
            [
                new CustomWeaponItemTemplate
                {
                    Id = "custom.ak47.gold",
                    DisplayName = "Golden AK-47",
                    Weapon = "weapon_ak47:ak47_gold_hy",
                    BaseWeapon = "weapon_ak47",
                    VdataName = "ak47_gold_hy",
                    PrecacheModel = "weapons/nozb1/gold_ak47/weapon_gold_ak47_ag2.vmdl",
                    Price = 3500,
                    SellPrice = 1750,
                    DurationSeconds = 3600,
                    Type = nameof(ShopItemType.Temporary),
                    Team = nameof(ShopItemTeam.Any),
                    Category = "Rifles",
                    Enabled = true,
                    CanBeSold = true,
                    AllowPreview = true,
                    IsEquipable = true,
                    GrantOnPurchase = true,
                    GrantOnRoundStart = true,
                    SelectWeaponOnEquip = true
                },
                new CustomWeaponItemTemplate
                {
                    Id = "custom.butterfly.hiro",
                    DisplayName = "Butterfly Hiro",
                    Weapon = string.Empty,
                    BaseWeapon = "weapon_knife_butterfly",
                    VdataName = "scripts/weapons/weapon_knife_butterfly.vdata",
                    PrecacheModel = "weapons/models/butterflu_knife_hiro/butterfly_knife_hiro_ag2.vmdl",
                    Price = 2200,
                    SellPrice = 1100,
                    DurationSeconds = 3600,
                    Type = nameof(ShopItemType.Temporary),
                    Team = nameof(ShopItemTeam.Any),
                    Category = "Knifes",
                    Enabled = true,
                    CanBeSold = true,
                    AllowPreview = true,
                    IsEquipable = true,
                    GrantOnPurchase = true,
                    GrantOnRoundStart = true,
                    SelectWeaponOnEquip = false
                }
            ]
        };
    }
}
