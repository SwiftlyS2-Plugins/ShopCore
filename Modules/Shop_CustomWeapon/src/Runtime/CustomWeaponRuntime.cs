namespace ShopCore;

internal sealed record CustomWeaponRuntime(
    string ItemId,
    string DisplayName,
    string BaseWeapon,
    string VdataName,
    string PrecacheModel,
    bool GrantOnPurchase,
    bool GrantOnRoundStart,
    bool SelectWeaponOnEquip
);
