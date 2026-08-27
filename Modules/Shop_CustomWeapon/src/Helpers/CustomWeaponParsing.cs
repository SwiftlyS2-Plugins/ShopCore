using System;
using System.Linq;

namespace ShopCore;

internal static class CustomWeaponParsing
{
    public static string ResolveItemCategory(string rootCategory, CustomWeaponItemTemplate template, string baseWeapon, string defaultCategory)
    {
        var normalizedRoot = string.IsNullOrWhiteSpace(rootCategory)
            ? string.Empty
            : rootCategory.Trim().Trim('/');

        if (!string.IsNullOrWhiteSpace(template.Category))
        {
            return CombineCategory(normalizedRoot, template.Category, defaultCategory);
        }

        var autoCategory = GetAutomaticCategory(baseWeapon);
        return CombineCategory(normalizedRoot, autoCategory, defaultCategory);
    }

    public static string CombineCategory(string rootCategory, string childCategory, string defaultCategory)
    {
        var normalizedRoot = string.IsNullOrWhiteSpace(rootCategory)
            ? string.Empty
            : rootCategory.Trim().Trim('/');
        var normalizedChild = string.IsNullOrWhiteSpace(childCategory)
            ? string.Empty
            : childCategory.Trim().Trim('/');

        if (string.IsNullOrWhiteSpace(normalizedRoot))
        {
            return string.IsNullOrWhiteSpace(normalizedChild)
                ? defaultCategory
                : normalizedChild;
        }

        return string.IsNullOrWhiteSpace(normalizedChild)
            ? normalizedRoot
            : $"{normalizedRoot}/{normalizedChild}";
    }

    public static string GetAutomaticCategory(string baseWeapon)
    {
        if (WeaponHelpers.IsKnifeWeapon(baseWeapon))
        {
            return "Knifes";
        }

        return baseWeapon.ToLowerInvariant() switch
        {
            "weapon_deagle" => "Pistols",
            "weapon_hkp2000" => "Pistols",
            "weapon_glock" => "Pistols",
            "weapon_elite" => "Pistols",
            "weapon_usp_silencer" => "Pistols",
            "weapon_cz75a" => "Pistols",
            "weapon_p250" => "Pistols",
            "weapon_fiveseven" => "Pistols",
            "weapon_tec9" => "Pistols",
            "weapon_revolver" => "Pistols",
            "weapon_ak47" => "Rifles",
            "weapon_m4a1" => "Rifles",
            "weapon_m4a1_silencer" => "Rifles",
            "weapon_m4a4" => "Rifles",
            "weapon_famas" => "Rifles",
            "weapon_galilar" => "Rifles",
            "weapon_aug" => "Rifles",
            "weapon_sg556" => "Rifles",
            "weapon_awp" => "Snipers",
            "weapon_ssg08" => "Snipers",
            "weapon_scar20" => "Snipers",
            "weapon_g3sg1" => "Snipers",
            "weapon_bizon" => "SMGs",
            "weapon_mp5sd" => "SMGs",
            "weapon_mp7" => "SMGs",
            "weapon_mp9" => "SMGs",
            "weapon_p90" => "SMGs",
            "weapon_ump45" => "SMGs",
            "weapon_mac10" => "SMGs",
            "weapon_m249" => "Machine Guns",
            "weapon_negev" => "Machine Guns",
            "weapon_xm1014" => "Shotguns",
            "weapon_nova" => "Shotguns",
            "weapon_mag7" => "Shotguns",
            "weapon_sawedoff" => "Shotguns",
            "weapon_hegrenade" => "Grenades",
            "weapon_flashbang" => "Grenades",
            "weapon_smokegrenade" => "Grenades",
            "weapon_molotov" => "Grenades",
            "weapon_incgrenade" => "Grenades",
            "weapon_decoy" => "Grenades",
            "weapon_taser" => "Gear",
            _ => ToCategoryName(baseWeapon)
        };
    }

    public static string ToCategoryName(string baseWeapon)
    {
        var value = string.IsNullOrWhiteSpace(baseWeapon)
            ? "Other"
            : baseWeapon.Trim();

        if (value.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase))
        {
            value = value[7..];
        }

        var parts = value
            .Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(static part => part.Length == 0 ? string.Empty : char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant());

        var result = string.Join(' ', parts);
        return string.IsNullOrWhiteSpace(result) ? "Other" : result;
    }

    public static bool TryParseWeaponSpec(string weaponSpec, out string weaponName, out string appearanceValue, out bool usesModelPath)
    {
        weaponName = string.Empty;
        appearanceValue = string.Empty;
        usesModelPath = false;

        if (string.IsNullOrWhiteSpace(weaponSpec))
        {
            return false;
        }

        var separator = weaponSpec.Contains('|') ? '|' : ':';
        var parts = weaponSpec.Split(separator, 2, StringSplitOptions.TrimEntries);
        weaponName = parts[0].Trim();
        appearanceValue = parts.Length > 1 ? parts[1].Trim() : string.Empty;
        usesModelPath = appearanceValue.EndsWith(".vmdl", StringComparison.OrdinalIgnoreCase);

        return !string.IsNullOrWhiteSpace(weaponName) && !string.IsNullOrWhiteSpace(appearanceValue);
    }
}
