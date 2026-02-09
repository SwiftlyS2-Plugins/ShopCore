<div align="center">
  <img src="https://pan.samyyc.dev/s/VYmMXE" />
  <h2><strong>ShopCore</strong></h2>
  <h3>A modular shop core plugin for SwiftlyS2 with credits, inventory, categories, and sell support.</h3>
</div>

<p align="center">
  <img src="https://img.shields.io/badge/build-passing-brightgreen" alt="Build Status">
  <img src="https://img.shields.io/github/downloads/SwiftlyS2-Plugins/ShopCore/total" alt="Downloads">
  <img src="https://img.shields.io/github/stars/SwiftlyS2-Plugins/ShopCore?style=flat&logo=github" alt="Stars">
  <img src="https://img.shields.io/github/license/SwiftlyS2-Plugins/ShopCore" alt="License">
</p>

## Overview

ShopCore is the base shop system for SwiftlyS2. It provides:

- A shared contract (`IShopCoreApiV1`) for other plugins to register items and interact with credits.
- Buy and inventory menus with category navigation.
- Credit economy integration via `Economy.API.v1`.
- Item state persistence and expiration via `Cookies.Player.V1`.
- Optional item selling, gifting, starting credits, and timed income.

## Requirements

This plugin depends on these shared interfaces:

- `Cookies.Player.V1` [Click Here](https://github.com/SwiftlyS2-Plugins/Cookies/releases/tag/v1.0.5)
- `Economy.API.v1` [Click Here](https://github.com/SwiftlyS2-Plugins/Economy/releases/tag/v2.0.1)

Make sure the Cookies and Economy plugins are loaded and export matching contract DLLs.

## Commands

All command aliases are configurable in `shopcore.jsonc`.

| Command (default alias)                                                     | Description                                                |
| :-------------------------------------------------------------------------- | :--------------------------------------------------------- |
| `!shop` / `!store`                                                          | Opens the main shop menu.                                  |
| `!buy`                                                                      | Opens the buy categories menu directly.                    |
| `!inventory` / `!inv`                                                       | Opens the inventory categories menu directly.              |
| `!credits` / `!balance`                                                     | Shows your current credits balance.                        |
| `!giftcredits <target> <amount>` / `!gift <target> <amount>`                | Transfers credits to another player (if enabled).          |
| `!givecredits <target> <amount>` / `!addcredits ...`                        | Adds credits to a player (admin permission required).      |
| `!removecredits <target> <amount>` / `!takecredits ...` / `!subcredits ...` | Removes credits from a player (admin permission required). |

Default admin permission: `shopcore.admin.credits`

## Configuration (`shopcore.jsonc`)

ShopCore reads config from the `Main` section.

### Commands (`Main.Commands`)

| Setting                 | Default                   | Description                                                         |
| :---------------------- | :------------------------ | :------------------------------------------------------------------ |
| `RegisterAsRawCommands` | `true`                    | Registers commands as raw commands through Swiftly command service. |
| `OpenShopMenu`          | `["shop", "store"]`       | Aliases for main shop menu.                                         |
| `OpenBuyMenu`           | `["buy"]`                 | Aliases for buy menu.                                               |
| `OpenInventoryMenu`     | `["inventory", "inv"]`    | Aliases for inventory menu.                                         |
| `ShowCredits`           | `["credits", "balance"]`  | Aliases to display current balance.                                 |
| `GiftCredits`           | `["giftcredits", "gift"]` | Aliases for player-to-player transfer.                              |

### Admin Commands (`Main.Commands.Admin`)

| Setting         | Default                                          | Description                                    |
| :-------------- | :----------------------------------------------- | :--------------------------------------------- |
| `Permission`    | `shopcore.admin.credits`                         | Permission required for admin credit commands. |
| `GiveCredits`   | `["givecredits", "addcredits"]`                  | Aliases to add credits to a target.            |
| `RemoveCredits` | `["removecredits", "takecredits", "subcredits"]` | Aliases to remove credits from a target.       |

### Credits (`Main.Credits`)

| Setting                             | Default   | Description                                                  |
| :---------------------------------- | :-------- | :----------------------------------------------------------- |
| `WalletName`                        | `credits` | Economy wallet kind used by ShopCore.                        |
| `StartingBalance`                   | `0`       | Minimum initial balance to enforce.                          |
| `GrantStartingBalanceOncePerPlayer` | `true`    | Applies starting balance only once per player using cookies. |
| `NotifyWhenStartingBalanceApplied`  | `true`    | Sends a localized message when starting balance is applied.  |

### Timed Income (`Main.Credits.TimedIncome`)

| Setting             | Default | Description                                  |
| :------------------ | :------ | :------------------------------------------- |
| `Enabled`           | `false` | Enables periodic credit rewards.             |
| `AmountPerInterval` | `0`     | Credits granted each interval.               |
| `IntervalSeconds`   | `300`   | Interval in seconds.                         |
| `NotifyPlayers`     | `false` | Sends message each time credits are granted. |

### Credit Transfer (`Main.Credits.Transfer`)

| Setting             | Default | Description                               |
| :------------------ | :------ | :---------------------------------------- |
| `Enabled`           | `true`  | Enables gifting credits between players.  |
| `MinimumAmount`     | `1`     | Minimum transferable amount.              |
| `AllowSelfTransfer` | `false` | Allows gifting credits to yourself.       |
| `NotifyReceiver`    | `true`  | Notifies receiver on successful transfer. |

### Admin Credit Adjustments (`Main.Credits.AdminAdjustments`)

| Setting                          | Default | Description                                                  |
| :------------------------------- | :------ | :----------------------------------------------------------- |
| `NotifyTargetPlayer`             | `true`  | Notifies player when admin changes their balance.            |
| `ClampRemovalToAvailableBalance` | `true`  | Caps remove command to available credits instead of failing. |

### Menus (`Main.Menus`)

| Setting                        | Default             | Description                                       |
| :----------------------------- | :------------------ | :------------------------------------------------ |
| `FreezePlayerWhileOpen`        | `false`             | Freezes player while menu is open.                |
| `EnableMenuSound`              | `true`              | Enables menu sound effects.                       |
| `MaxVisibleItems`              | `5`                 | Max visible items in menu page (clamped to 1..5). |
| `DefaultCommentTranslationKey` | `shop.menu.comment` | Translation key for default menu comment line.    |

### Behavior (`Main.Behavior`)

| Setting                  | Default | Description                                                  |
| :----------------------- | :------ | :----------------------------------------------------------- |
| `AllowSelling`           | `true`  | Enables/disables selling items.                              |
| `DefaultSellRefundRatio` | `0.50`  | Fallback refund ratio when item does not define `SellPrice`. |

### Example

```jsonc
{
  "Main": {
    "Commands": {
      "RegisterAsRawCommands": true,
      "OpenShopMenu": ["shop", "store"],
      "OpenBuyMenu": ["buy"],
      "OpenInventoryMenu": ["inventory", "inv"],
      "ShowCredits": ["credits", "balance"],
      "GiftCredits": ["giftcredits", "gift"],
      "Admin": {
        "Permission": "shopcore.admin.credits",
        "GiveCredits": ["givecredits", "addcredits"],
        "RemoveCredits": ["removecredits", "takecredits", "subcredits"],
      },
    },
    "Credits": {
      "WalletName": "credits",
      "StartingBalance": 0,
      "GrantStartingBalanceOncePerPlayer": true,
      "NotifyWhenStartingBalanceApplied": true,
      "TimedIncome": {
        "Enabled": false,
        "AmountPerInterval": 0,
        "IntervalSeconds": 300,
        "NotifyPlayers": false,
      },
      "Transfer": {
        "Enabled": true,
        "MinimumAmount": 1,
        "AllowSelfTransfer": false,
        "NotifyReceiver": true,
      },
      "AdminAdjustments": {
        "NotifyTargetPlayer": true,
        "ClampRemovalToAvailableBalance": true,
      },
    },
    "Menus": {
      "FreezePlayerWhileOpen": false,
      "EnableMenuSound": true,
      "MaxVisibleItems": 5,
      "DefaultCommentTranslationKey": "shop.menu.comment",
    },
    "Behavior": {
      "AllowSelling": true,
      "DefaultSellRefundRatio": 0.5,
    },
  },
}
```

## Module Config System

ShopCore supports module-owned item config templates, but keeps runtime module configs centralized under ShopCore.

### How It Works

When a module calls `IShopCoreApiV1.LoadModuleTemplateConfig<T>(moduleId, fileName, sectionName)`:

1. ShopCore looks for the module template source in:
   `<ModulePlugin>/resources/templates/<fileName>`
2. ShopCore resolves the centralized template target in:
   `ShopCore/resources/templates/modules/<ModuleId>/<fileName>`
3. If the centralized file does not exist and module source exists, ShopCore copies source to centralized target once.
4. ShopCore always reads/deserializes from the centralized file.

This means each module can ship defaults, while server owners manage all module configs from one location.

### Behavior Notes

- Centralized file has priority after first creation.
- Copy happens only when centralized file is missing.
- JSONC is supported (comments + trailing commas).
- `sectionName` (usually `Main`) is optional; if not found, root object is used.
- Invalid relative paths (absolute paths / `..`) are rejected for safety.

### Recommended Layout

- Ship default template in module plugin:
  `Modules/<ModuleName>/resources/templates/items_config.jsonc`
- Keep editable centralized file in ShopCore:
  `ShopCore/resources/templates/modules/<ModuleId>/items_config.jsonc`

### Example (Healthshot Module)

Centralized template currently used by the sample module:

- `ShopCore/resources/templates/modules/Shop_Healthshot/items_config.jsonc`

## Shop Contract Summary (`ShopCore.Contract`)

### Amount rules

Although the contract uses `decimal`, current Economy integration is integer-based.  
Use positive whole-number credit amounts for item prices and credit operations.

### Item model

`ShopItemDefinition` fields:

- `Id`, `DisplayName`, `Category`
- `Price`, optional `SellPrice`
- optional `Duration`
- `Type` (`Passive`, `Consumable`, `Temporary`, `Permanent`)
- `Team` (`Any`, `T`, `CT`)
- `Enabled`, `CanBeSold`

### Core API (`IShopCoreApiV1`)

Main capabilities exposed to other plugins:

- Register/unregister/query items.
- Load typed module template configs through ShopCore central template path.
- Read/add/subtract/check player credits.
- Purchase and sell items with detailed `ShopTransactionResult`.
- Enable/disable item per player.
- Read item expiration timestamp.
- Subscribe to events:
  - `OnBeforeItemPurchase` (cancelable)
  - `OnBeforeItemSell` (cancelable)
  - `OnBeforeItemToggle` (cancelable)
  - `OnItemRegistered`
  - `OnItemPurchased`
  - `OnItemSold`
  - `OnItemToggled`
  - `OnItemExpired`

### Transaction result

`ShopTransactionResult` returns:

- `Status` (`Success`, `ItemNotFound`, `ItemDisabled`, `TeamNotAllowed`, `AlreadyOwned`, `NotOwned`, `NotSellable`, `InsufficientCredits`, `InvalidAmount`, `InternalError`, `BlockedByModule`)
- `Message`
- `Item`
- `CreditsAfter`
- `CreditsDelta`
- `ExpiresAtUnixSeconds`

## Quick Integration Example

```csharp
using ShopCore.Contract;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Plugins;

public class MyModule : BasePlugin
{
    private IShopCoreApiV1 shop = null!;

    public MyModule(ISwiftlyCore core) : base(core) { }

    public override void UseSharedInterface(IInterfaceManager interfaceManager)
    {
        shop = interfaceManager.GetSharedInterface<IShopCoreApiV1>("ShopCore.API.v1");
    }

    public override void Load(bool hotReload)
    {
        var config = shop.LoadModuleTemplateConfig<MyModuleConfig>(
            "MyModule",
            "items_config.jsonc",
            "Main"
        );

        shop.RegisterItem(new ShopItemDefinition(
            Id: "healthshot",
            DisplayName: "Health Shot",
            Category: "Healings",
            Price: config.DefaultPrice,
            SellPrice: 250,
            Duration: TimeSpan.FromSeconds(30),
            Type: ShopItemType.Temporary,
            Team: ShopItemTeam.Any,
            Enabled: true,
            CanBeSold: true
        ));
    }

    public override void Unload() { }
}
public class MyModuleConfig
{
  public decimal DefaultPrice { get; set; } = 400;
}
```

### Cancelable Hook Example

```csharp
// Example: block expensive items for non-admin players.
shop.OnBeforeItemPurchase += context =>
{
    if (context.Item.Price > 5000 && !Core.Permission.PlayerHasPermission(context.Player.SteamID, "shop.vip"))
    {
        context.BlockLocalized("shop.error.vip_required", context.Item.DisplayName);
        // or: context.Block("You need VIP to buy this item.");
    }
};
```

## Localization

Default translations are in:

- `ShopCore/resources/translations/en.jsonc`

## Installation

1. Build or download the plugin.
2. Copy plugin output to:
   `.../game/csgo/addons/swiftlys2/plugins/ShopCore/`
3. Ensure these files exist in that folder:
   - `ShopCore.dll`
   - `resources/exports/ShopCore.Contract.dll`
   - `resources/translations/en.jsonc`
   - `resources/templates/modules/...` (if using centralized module configs)
4. Ensure Economy and Cookies plugins are also installed and loaded.
5. Start/restart the server.

## Building

- Open `ShopCore.sln` in your .NET IDE.
- Build solution.
- Output is generated under:
  - `build/plugins/ShopCore/`
  - `build/plugins/ShopCore/resources/exports/`
