# Shop_CustomWeapon

`Shop_CustomWeapon` is a ShopCore module for SwiftlyS2 that lets you sell custom weapon appearances and custom knife variants through the shop.

It supports:

- regular weapon subclass skins
- custom weapon models
- custom knife variants
- auto-reapply on spawn
- warmup support
- preview text through ShopCore

## Config location

The module config is stored in:

```text
addons/swiftlys2/configs/plugins/ShopCore/modules/customweapon_items.jsonc
```

The module uses the `Main` section in that file.

## How it works

Each item defines a base weapon plus appearance data.

For regular weapons, the module can:

- apply a subclass or vdata skin
- apply a custom model
- optionally give the weapon when purchased

For knives, the module:

- gives a base knife entity
- sets the correct `ItemDefinitionIndex` for the knife variant
- applies the configured model
- restores the default team knife when the item is unequipped, sold, or expires

## Supported item fields

Each entry in `Items` supports the following fields:

| Field | Type | Required | Description |
|---|---|---:|---|
| `Id` | `string` | Yes | Unique shop item id. |
| `DisplayName` | `string` | No | Name shown in the shop. If omitted, `Id` is used. |
| `Category` | `string` | No | Optional subcategory for this item. It is appended under `Settings.Category`. |
| `Weapon` | `string` | No | Combined weapon spec in the format `weapon_name:appearance` or `weapon_name|appearance`. |
| `BaseWeapon` | `string` | Yes* | Base weapon classname. Required unless supplied through `Weapon`. |
| `VdataName` | `string` | No | Subclass or `.vdata` path used for appearance changes. |
| `PrecacheModel` | `string` | No | Model path to precache and apply. Use `.vmdl`. |
| `Price` | `decimal` | Yes | Purchase price. |
| `SellPrice` | `decimal?` | No | Sell value. |
| `DurationSeconds` | `int` | No | Duration in seconds for temporary items. |
| `Type` | `string` | No | ShopCore item type. Defaults to `Temporary`. |
| `Team` | `string` | No | Team restriction. Defaults to `Any`. |
| `Enabled` | `bool` | No | Whether the item is enabled. Defaults to `true`. |
| `CanBeSold` | `bool` | No | Whether the item can be sold. Defaults to `true`. |
| `AllowPreview` | `bool` | No | Whether preview is allowed. Defaults to `true`. |
| `IsEquipable` | `bool` | No | Whether the item can be equipped. Defaults to `true`. |
| `GrantOnPurchase` | `bool` | No | Give/apply on purchase. Defaults to `true`. |
| `GrantOnRoundStart` | `bool` | No | Reapply on round start. Defaults to `true`. |
| `SelectWeaponOnEquip` | `bool` | No | Select the weapon after giving it. Defaults to `true`. |

`BaseWeapon` and appearance data are required overall. You must provide:

- `BaseWeapon` directly, or through `Weapon`
- and at least one of:
  - `VdataName`
  - `PrecacheModel`
  - appearance data in `Weapon`

## Weapon field format

`Weapon` is a shorthand field.

Examples:

```json
"Weapon": "weapon_ak47:ak47_gold_hy"
```

```json
"Weapon": "weapon_awp|weapons/models/custom/awp_dragon.vmdl"
```

If you use explicit fields like `BaseWeapon`, `VdataName`, and `PrecacheModel`, they override the parsed values from `Weapon`.

## Categories

The module now supports per-item categories.

If `Settings.Category` is:

```json
"Category": "Weapons/Custom"
```

and an item has:

```json
"Category": "Knives"
```

the final shop category becomes:

```text
Weapons/Custom/Knives
```

If an item does not define `Category`, the module auto-groups it by base weapon.

Examples:

- `weapon_knife_butterfly` -> `Weapons/Custom/Knives`
- `weapon_ak47` -> `Weapons/Custom/AK-47`
- `weapon_m4a1` -> `Weapons/Custom/M4A1-S`
- `weapon_mp9` -> `Weapons/Custom/MP9`
- `weapon_hegrenade` -> `Weapons/Custom/HE Grenade`

This means you can either:

- let the module categorize items automatically
- or set `Category` manually per item for your own grouping

## Regular weapon example

```json
{
  "Id": "custom.ak47.gold",
  "DisplayName": "Golden AK-47",
  "Category": "Rifles",
  "Weapon": "weapon_ak47:ak47_gold_hy",
  "BaseWeapon": "weapon_ak47",
  "VdataName": "ak47_gold_hy",
  "PrecacheModel": "",
  "Price": 3500,
  "SellPrice": 1750,
  "DurationSeconds": 3600,
  "Type": "Temporary",
  "Team": "Any",
  "Enabled": true,
  "CanBeSold": true,
  "AllowPreview": true,
  "IsEquipable": true,
  "GrantOnPurchase": true,
  "GrantOnRoundStart": true,
  "SelectWeaponOnEquip": true
}
```

## Knife example

```json
{
  "Id": "custom.butterfly.hiro",
  "DisplayName": "Butterfly Hiro",
  "Category": "Knives",
  "Weapon": "",
  "BaseWeapon": "weapon_knife_butterfly",
  "VdataName": "scripts/weapons/weapon_knife_butterfly.vdata",
  "PrecacheModel": "weapons/models/butterflu_knife_hiro/butterfly_knife_hiro_ag2.vmdl",
  "Price": 2200,
  "SellPrice": 1100,
  "DurationSeconds": 3600,
  "Type": "Temporary",
  "Team": "Any",
  "Enabled": true,
  "CanBeSold": true,
  "AllowPreview": true,
  "IsEquipable": true,
  "GrantOnPurchase": true,
  "GrantOnRoundStart": true,
  "SelectWeaponOnEquip": false
}
```

## Knife behavior

Custom knives behave differently from regular weapons.

The module will:

- detect the equipped knife slot
- replace it with the configured custom knife setup
- set the correct knife definition index
- apply the custom model
- keep reapplying while the item stays equipped

When a knife is removed, the module will:

- drop the custom knife immediately
- give back the default knife instantly
- use `weapon_knife_t` for T
- use `weapon_knife` for CT

## Auto-apply behavior

Enabled items are reapplied automatically in these situations:

- when the player spawns
- when the player joins and then spawns
- during warmup spawns
- on round start

Behavior details:

- **spawn/join/warmup**: all enabled items are reapplied
- **round start**: only items with `GrantOnRoundStart = true` are reapplied

This means a knife can stay active continuously until the player unequips it.

## Model notes

Use `.vmdl` paths in `PrecacheModel`.

Example:

```json
"PrecacheModel": "weapons/models/butterflu_knife_hiro/butterfly_knife_hiro_ag2.vmdl"
```

Do not use `.vmdl_c` here.

## Build

From the module directory:

```powershell
dotnet build
```

## Notes

- Knife variants are resolved through `ItemDefinitionIndex`.
- Knife models are restored to the default knife instantly on unequip.
- The module will generate a default config if no items are present.