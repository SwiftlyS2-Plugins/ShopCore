using System;
using SwiftlyS2.Shared.SchemaDefinitions;

namespace ShopCore;

public sealed partial class Shop_CustomWeapon
{
    private void ApplyAppearance(CBasePlayerWeapon weapon, CustomWeaponRuntime runtime)
    {
        var isKnife = WeaponHelpers.IsKnifeWeapon(runtime.BaseWeapon);
        var isThrowable = WeaponHelpers.IsThrowableWeapon(runtime.BaseWeapon);
        var hasCustomModel = !string.IsNullOrWhiteSpace(runtime.PrecacheModel);

        var vdata = ResolveVdata(runtime);
        if (!isThrowable && !string.IsNullOrWhiteSpace(vdata))
        {
            var currentSubclass = weapon.Entity?.DesignerName ?? runtime.BaseWeapon;
            SetSubclass(weapon, currentSubclass, vdata);
        }

        if (hasCustomModel)
        {
            SetWeaponModel(weapon, runtime.PrecacheModel);
        }

        SetWeaponCustomName(weapon, runtime.DisplayName);
    }

    private static string ResolveVdata(CustomWeaponRuntime runtime)
    {
        if (!string.IsNullOrWhiteSpace(runtime.VdataName))
        {
            var vdata = runtime.VdataName.Trim();
            if (vdata.EndsWith(".vdata", StringComparison.OrdinalIgnoreCase))
            {
                var fileName = System.IO.Path.GetFileNameWithoutExtension(vdata);
                return string.IsNullOrWhiteSpace(fileName) ? vdata : fileName;
            }
            return vdata;
        }

        if (WeaponHelpers.IsKnifeWeapon(runtime.BaseWeapon) &&
            !string.Equals(runtime.BaseWeapon, "weapon_knife", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(runtime.BaseWeapon, "weapon_knife_t", StringComparison.OrdinalIgnoreCase))
        {
            return runtime.BaseWeapon.ToLowerInvariant();
        }

        return string.Empty;
    }

    private void SetSubclass(CBasePlayerWeapon weapon, string oldSubclass, string newSubclass)
    {
        if (!weapon.IsValid || string.IsNullOrWhiteSpace(newSubclass))
        {
            return;
        }

        originalSubclassByWeaponAddress[weapon.Address] = oldSubclass;
        weapon.AcceptInput("ChangeSubclass", newSubclass, weapon, weapon);
    }

    private void ResetSubclass(CBasePlayerWeapon weapon)
    {
        if (!weapon.IsValid)
        {
            return;
        }

        if (!originalSubclassByWeaponAddress.TryGetValue(weapon.Address, out var oldSubclass) || string.IsNullOrWhiteSpace(oldSubclass))
        {
            return;
        }

        weapon.AcceptInput("ChangeSubclass", oldSubclass, weapon, weapon);
        originalSubclassByWeaponAddress.Remove(weapon.Address);
    }

    private void SetWeaponModel(CBasePlayerWeapon weapon, string modelPath)
    {
        if (!weapon.IsValid || string.IsNullOrWhiteSpace(modelPath))
        {
            return;
        }

        var currentModel = weapon.CBodyComponent?.SceneNode?.GetSkeletonInstance().ModelState.ModelName ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(currentModel) && !originalModelByWeaponAddress.ContainsKey(weapon.Address))
        {
            originalModelByWeaponAddress[weapon.Address] = currentModel;
        }

        weapon.SetModel(modelPath);
    }

    private void SetWeaponCustomName(CBasePlayerWeapon weapon, string displayName)
    {
        if (!weapon.IsValid || string.IsNullOrWhiteSpace(displayName))
        {
            return;
        }

        var item = weapon.AttributeManager.Item;
        var currentName = item.CustomName ?? string.Empty;
        if (!originalNameByWeaponAddress.ContainsKey(weapon.Address))
        {
            originalNameByWeaponAddress[weapon.Address] = currentName;
        }

        item.CustomName = displayName;
        item.CustomNameUpdated();
    }

    private void ResetWeaponCustomName(CBasePlayerWeapon weapon)
    {
        if (!weapon.IsValid)
        {
            return;
        }

        if (!originalNameByWeaponAddress.TryGetValue(weapon.Address, out var oldName))
        {
            return;
        }

        weapon.AttributeManager.Item.CustomName = oldName;
        weapon.AttributeManager.Item.CustomNameUpdated();
        originalNameByWeaponAddress.Remove(weapon.Address);
    }

    private void ResetWeaponAppearance(CBasePlayerWeapon weapon, CustomWeaponRuntime runtime)
    {
        ResetWeaponCustomName(weapon);

        if (!string.IsNullOrWhiteSpace(runtime.PrecacheModel))
        {
            ResetWeaponModel(weapon);
        }

        if (!WeaponHelpers.IsKnifeWeapon(runtime.BaseWeapon)
            && !WeaponHelpers.IsThrowableWeapon(runtime.BaseWeapon)
            && !string.IsNullOrWhiteSpace(ResolveVdata(runtime)))
        {
            ResetSubclass(weapon);
        }
    }

    private void ResetWeaponModel(CBasePlayerWeapon weapon)
    {
        if (!weapon.IsValid)
        {
            return;
        }

        if (!originalModelByWeaponAddress.TryGetValue(weapon.Address, out var oldModel) || string.IsNullOrWhiteSpace(oldModel))
        {
            return;
        }

        weapon.SetModel(oldModel);
        originalModelByWeaponAddress.Remove(weapon.Address);
    }
}
