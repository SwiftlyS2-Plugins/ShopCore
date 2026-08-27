using System;
using System.Collections.Generic;
using System.Linq;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;

namespace ShopCore;

internal static class WeaponHelpers
{
    internal static readonly Dictionary<string, ushort> KnifeDefinitionIndexByClassname = new(StringComparer.OrdinalIgnoreCase)
    {
        ["weapon_knife"] = 42,
        ["weapon_knife_t"] = 59,
        ["weapon_bayonet"] = 500,
        ["weapon_knife_css"] = 503,
        ["weapon_knife_flip"] = 505,
        ["weapon_knife_gut"] = 506,
        ["weapon_knife_karambit"] = 507,
        ["weapon_knife_m9_bayonet"] = 508,
        ["weapon_knife_tactical"] = 509,
        ["weapon_knife_falchion"] = 512,
        ["weapon_knife_survival_bowie"] = 514,
        ["weapon_knife_butterfly"] = 515,
        ["weapon_knife_push"] = 516,
        ["weapon_knife_cord"] = 517,
        ["weapon_knife_canis"] = 518,
        ["weapon_knife_ursus"] = 519,
        ["weapon_knife_gypsy_jackknife"] = 520,
        ["weapon_knife_outdoor"] = 521,
        ["weapon_knife_stiletto"] = 522,
        ["weapon_knife_widowmaker"] = 523,
        ["weapon_knife_skeleton"] = 525,
        ["weapon_knife_kukri"] = 526,
    };

    public static CBasePlayerWeapon? FindWeapon(IPlayer player, string baseWeapon)
    {
        var weaponServices = player.PlayerPawn?.WeaponServices;
        if (weaponServices is null || !weaponServices.IsValid)
        {
            return null;
        }

        return weaponServices.MyValidWeapons.FirstOrDefault(weapon =>
            weapon.IsValid && string.Equals(GetDesignerName(weapon), baseWeapon, StringComparison.Ordinal));
    }

    public static CBasePlayerWeapon? FindPlayerKnife(IPlayer player)
    {
        var weaponServices = player.PlayerPawn?.WeaponServices;
        if (weaponServices is null || !weaponServices.IsValid)
        {
            return null;
        }

        return weaponServices.MyValidWeapons.FirstOrDefault(weapon =>
        {
            if (!weapon.IsValid)
            {
                return false;
            }

            var name = weapon.Entity?.DesignerName ?? string.Empty;
            return name.StartsWith("weapon_knife", StringComparison.OrdinalIgnoreCase)
                || name.Contains("bayonet", StringComparison.OrdinalIgnoreCase);
        });
    }

    public static bool IsPlayerAlive(IPlayer player)
    {
        var pawn = player.PlayerPawn;
        return player.IsValid && !player.IsFakeClient && pawn is not null && pawn.IsValid && pawn.LifeState == (int)LifeState_t.LIFE_ALIVE;
    }

    public static string GetDesignerName(CBasePlayerWeapon weapon)
    {
        var weaponDesignerName = weapon.Entity?.DesignerName ?? string.Empty;
        var weaponIndex = weapon.AttributeManager.Item.ItemDefinitionIndex;

        return (weaponDesignerName, weaponIndex) switch
        {
            var (name, _) when name.Contains("bayonet", StringComparison.OrdinalIgnoreCase) => "weapon_knife",
            ("weapon_deagle", 64) => "weapon_revolver",
            ("weapon_m4a1", 60) => "weapon_m4a1_silencer",
            ("weapon_hkp2000", 61) => "weapon_usp_silencer",
            ("weapon_mp7", 23) => "weapon_mp5sd",
            _ => weaponDesignerName
        };
    }

    public static bool IsKnifeWeapon(string baseWeapon)
    {
        return baseWeapon.StartsWith("weapon_knife", StringComparison.OrdinalIgnoreCase)
            || baseWeapon.Equals("weapon_bayonet", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsThrowableWeapon(string baseWeapon)
    {
        return baseWeapon.Equals("weapon_hegrenade", StringComparison.OrdinalIgnoreCase)
            || baseWeapon.Equals("weapon_flashbang", StringComparison.OrdinalIgnoreCase)
            || baseWeapon.Equals("weapon_smokegrenade", StringComparison.OrdinalIgnoreCase)
            || baseWeapon.Equals("weapon_molotov", StringComparison.OrdinalIgnoreCase)
            || baseWeapon.Equals("weapon_incgrenade", StringComparison.OrdinalIgnoreCase)
            || baseWeapon.Equals("weapon_decoy", StringComparison.OrdinalIgnoreCase);
    }
}
