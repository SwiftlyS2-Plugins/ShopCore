using System.Collections.Generic;
using ShopCore.Contract;

namespace ShopCore;

internal sealed class CustomWeaponModuleConfig
{
    public CustomWeaponModuleSettings Settings { get; set; } = new();
    public List<CustomWeaponItemTemplate> Items { get; set; } = [];
}

internal sealed class CustomWeaponModuleSettings
{
    public bool UseCorePrefix { get; set; } = true;
    public string Category { get; set; } = "Weapons/Custom";
}

internal sealed class CustomWeaponItemTemplate
{
    public string Id { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? Category { get; set; }
    public string Weapon { get; set; } = string.Empty;
    public string? BaseWeapon { get; set; }
    public string? VdataName { get; set; }
    public string? PrecacheModel { get; set; }
    public decimal Price { get; set; }
    public decimal? SellPrice { get; set; }
    public int DurationSeconds { get; set; }
    public string Type { get; set; } = nameof(ShopItemType.Temporary);
    public string Team { get; set; } = nameof(ShopItemTeam.Any);
    public bool Enabled { get; set; } = true;
    public bool CanBeSold { get; set; } = true;
    public bool AllowPreview { get; set; } = true;
    public bool IsEquipable { get; set; } = true;
    public bool GrantOnPurchase { get; set; } = true;
    public bool GrantOnRoundStart { get; set; } = true;
    public bool SelectWeaponOnEquip { get; set; } = true;
}
