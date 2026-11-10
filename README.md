<div align="center">
  <img src="https://pan.samyyc.dev/s/VYmMXE" alt="ShopCore logo" />
  <h1><strong>ShopCore</strong></h1>
  <p>A credits shop for CS2: modules add the items, ShopCore sells them.</p>
</div>

<p align="center">
  <img src="https://img.shields.io/badge/build-passing-brightgreen" alt="Build Status">
  <a href="https://github.com/SwiftlyS2-Plugins/ShopCore/releases/latest"><img src="https://img.shields.io/github/v/release/SwiftlyS2-Plugins/ShopCore?style=flat&logo=github&cacheSeconds=3600" alt="Latest Release"></a>
  <img src="https://img.shields.io/github/downloads/SwiftlyS2-Plugins/ShopCore/total?style=flat&logo=github&cacheSeconds=3600" alt="Downloads">
  <img src="https://img.shields.io/github/stars/SwiftlyS2-Plugins/ShopCore?style=flat&logo=github&cacheSeconds=3600" alt="Stars">
</p>

> [!NOTE]
> **ShopCore needs [Cookies](https://github.com/SwiftlyS2-Plugins/Cookies) and [Economy](https://github.com/SwiftlyS2-Plugins/Economy).**<br>
> Without them it loads but registers no commands. Install both first — [Installation](../../wiki/Installation).

---

## ✨ Features

The shop itself; [modules](#-modules) add the items.

- 🛒 **Shop menu** — **Buy** and **Inventory**, by category and subcategory, in `!shop`
- 💰 **Credits** — an Economy wallet, with a starting balance and timed income
- 🎁 **Gifting** — players give each other credits
- 🎒 **Owned items** — equip, unequip and sell from the inventory
- ⏳ **Timed items** — items that run out a set time after purchase
- 👀 **Previews** — try an item before buying it
- 🔒 **Limits** — items for one team, or for players with a permission
- 📒 **Ledger** — purchases, sales and credit changes, in memory, SQLite or MySQL
- 🔁 **Live reload** — `!shopcorereload` and `!reloadmodulesconfig`
- 🧩 **Module API** — other plugins add items, move credits and react to purchases

## 🚀 Getting Started

| Guide | Description |
|-------|-------------|
| **[Features](../../wiki/Features)** | Everything ShopCore does |
| **[Installation](../../wiki/Installation)** | Install and verify |
| **[Commands](../../wiki/Commands)** | Player and admin commands |
| **[Menus](../../wiki/Menus)** | The `!shop` menu |
| **[Configuration](../../wiki/Configuration)** | Every setting, with defaults |

## ⚙️ Running the Shop

| Topic | Description |
|-------|-------------|
| **[Credits](../../wiki/Credits)** | Starting balance, timed income, gifts, admin changes |
| **[Items](../../wiki/Items)** | Item fields, durations, selling, previews, categories |
| **[Storage](../../wiki/Storage)** | Where credits, items and the ledger are kept |

## 🧩 Modules

Official modules. **[Modules Overview](../../wiki/Modules)** covers installing them, their kinds and their files.

| Module | Kind | What it adds |
|--------|------|--------------|
| **[Shop_Bhop](../../wiki/Module-Bhop)** | equipable | Auto-bunnyhop, with an optional speed cap |
| **[Shop_Coinflip](../../wiki/Module-Coinflip)** | credits | `!coinflip` — bet credits on a coin toss |
| **[Shop_CustomWeapon](../../wiki/Module-CustomWeapon)** | equipable | Custom weapon models and skins, and custom knives |
| **[Shop_DecoyTeleport](../../wiki/Module-DecoyTeleport)** | one-time | A decoy that teleports you to where it lands |
| **[Shop_Flags](../../wiki/Module-Flags)** | equipable | A permission while the item is equipped |
| **[Shop_Healthshot](../../wiki/Module-Healthshot)** | one-time, equipable | A healthshot now, or one every round |
| **[Shop_HitSounds](../../wiki/Module-HitSounds)** | equipable | A sound each time you hit a player |
| **[Shop_Killscreen](../../wiki/Module-Killscreen)** | equipable | A screen effect on every kill |
| **[Shop_Parachute](../../wiki/Module-Parachute)** | equipable | Hold **E** to glide down |
| **[Shop_PlayerColor](../../wiki/Module-PlayerColor)** | equipable | A coloured or rainbow player model |
| **[Shop_PlayerModels](../../wiki/Module-PlayerModels)** | equipable | A different player model |
| **[Shop_Rewards](../../wiki/Module-Rewards)** | credits | Credits for kills, assists, MVPs and wins |
| **[Shop_SmokeColor](../../wiki/Module-SmokeColor)** | equipable | Coloured smoke from your smoke grenades |
| **[Shop_Tracers](../../wiki/Module-Tracers)** | equipable | A beam to every bullet impact |
| **[Shop_VIP](../../wiki/Module-VIP)** | one-time | VIPCore groups |

---

## 📚 Reference

| Topic | Description |
|-------|-------------|
| **[API Reference](../../wiki/API-Reference)** | Every member of `IShopCoreApiV2` |
| **[API Integration](../../wiki/API-Integration)** | Build your own module |
| **[Building](../../wiki/Building)** | Build from source and release |
| **[Troubleshooting](../../wiki/Troubleshooting)** | Symptoms, causes and fixes |

---

## 🤝 Report issues or request features

- **Issues**: [GitHub Issues](https://github.com/SwiftlyS2-Plugins/ShopCore/issues)
