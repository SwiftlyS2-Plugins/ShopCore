using ShopCore.Contract;
using SwiftlyS2.Core.Menus.OptionsBase;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Menus;
using SwiftlyS2.Shared.Players;

namespace ShopCore;

public partial class ShopCore
{
    private void HandleOpenShopMenuCommand(ICommandContext context)
    {
        if (context.Sender is not IPlayer player || !player.IsValid)
        {
            context.Reply("This command is available only in-game.");
            return;
        }

        OpenShopMainMenu(player);
    }

    private void HandleOpenBuyMenuCommand(ICommandContext context)
    {
        if (context.Sender is not IPlayer player || !player.IsValid)
        {
            context.Reply("This command is available only in-game.");
            return;
        }

        Core.MenusAPI.OpenMenuForPlayer(player, BuildBuyCategoryMenu(player));
    }

    private void HandleOpenInventoryMenuCommand(ICommandContext context)
    {
        if (context.Sender is not IPlayer player || !player.IsValid)
        {
            context.Reply("This command is available only in-game.");
            return;
        }

        Core.MenusAPI.OpenMenuForPlayer(player, BuildInventoryCategoryMenu(player));
    }

    private void HandleShowCreditsCommand(ICommandContext context)
    {
        if (context.Sender is not IPlayer player || !player.IsValid)
        {
            context.Reply("This command is available only in-game.");
            return;
        }

        var credits = shopApi.GetCredits(player);
        SendLocalizedChat(player, "shop.credits.current", FormatCredits(credits));
    }

    private void OpenShopMainMenu(IPlayer player, IMenuAPI? parent = null)
    {
        Core.MenusAPI.OpenMenuForPlayer(player, BuildMainMenu(player, parent));
    }

    private IMenuAPI BuildMainMenu(IPlayer player, IMenuAPI? parent = null)
    {
        var builder = CreateBaseMenuBuilder(player, "shop.menu.main.title", parent);
        var credits = shopApi.GetCredits(player);

        _ = builder.AddOption(new TextMenuOption(
            Localize(player, "shop.menu.main.credits", FormatCredits(credits)))
        {
            Enabled = false
        });

        var buyButton = new ButtonMenuOption(Localize(player, "shop.menu.main.buy"));
        buyButton.Click += (sender, args) =>
        {
            var parentMenu = (sender as IMenuOption)?.Menu;
            Core.MenusAPI.OpenMenuForPlayer(args.Player, BuildBuyCategoryMenu(args.Player, parentMenu));
            return ValueTask.CompletedTask;
        };
        _ = builder.AddOption(buyButton);

        var inventoryButton = new ButtonMenuOption(Localize(player, "shop.menu.main.inventory"));
        inventoryButton.Click += (sender, args) =>
        {
            var parentMenu = (sender as IMenuOption)?.Menu;
            Core.MenusAPI.OpenMenuForPlayer(args.Player, BuildInventoryCategoryMenu(args.Player, parentMenu));
            return ValueTask.CompletedTask;
        };
        _ = builder.AddOption(inventoryButton);

        return builder.Build();
    }

    private IMenuAPI BuildBuyCategoryMenu(IPlayer player, IMenuAPI? parent = null)
    {
        var builder = CreateBaseMenuBuilder(player, "shop.menu.buy.title", parent);
        var items = shopApi.GetItems()
            .Where(item => item.Enabled)
            .ToArray();

        var grouped = items
            .GroupBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (grouped.Length == 0)
        {
            _ = builder.AddOption(new TextMenuOption(Localize(player, "shop.menu.empty.buy")) { Enabled = false });
            return builder.Build();
        }

        foreach (var categoryGroup in grouped)
        {
            var category = categoryGroup.Key;
            var count = categoryGroup.Count();
            var categoryButton = new ButtonMenuOption(Localize(player, "shop.menu.category.entry", category, count));
            categoryButton.Click += (sender, args) =>
            {
                var parentMenu = (sender as IMenuOption)?.Menu;
                Core.MenusAPI.OpenMenuForPlayer(args.Player, BuildBuyItemsMenu(args.Player, category, parentMenu));
                return ValueTask.CompletedTask;
            };
            _ = builder.AddOption(categoryButton);
        }

        return builder.Build();
    }

    private IMenuAPI BuildBuyItemsMenu(IPlayer player, string category, IMenuAPI? parent = null)
    {
        var builder = CreateBaseMenuBuilder(player, "shop.menu.buy.category.title", parent, category);
        var items = shopApi.GetItemsByCategory(category)
            .Where(item => item.Enabled)
            .OrderBy(item => item.Price)
            .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (items.Length == 0)
        {
            _ = builder.AddOption(new TextMenuOption(Localize(player, "shop.menu.empty.category", category)) { Enabled = false });
            return builder.Build();
        }

        foreach (var item in items)
        {
            var itemButton = new ButtonMenuOption(BuildBuyItemText(player, item));
            itemButton.Comment = BuildBuyItemComment(player, item);
            itemButton.Click += (sender, args) =>
            {
                _ = shopApi.PurchaseItem(args.Player, item.Id);
                var currentMenu = (sender as IMenuOption)?.Menu;
                var parentMenu = currentMenu?.Parent.ParentMenu ?? parent;
                Core.MenusAPI.OpenMenuForPlayer(args.Player, BuildBuyItemsMenu(args.Player, category, parentMenu));
                return ValueTask.CompletedTask;
            };
            _ = builder.AddOption(itemButton);
        }

        return builder.Build();
    }

    private IMenuAPI BuildInventoryCategoryMenu(IPlayer player, IMenuAPI? parent = null)
    {
        var builder = CreateBaseMenuBuilder(player, "shop.menu.inventory.title", parent);
        var inventoryItems = shopApi.GetItems()
            .Where(item => shopApi.IsItemEnabled(player, item.Id))
            .ToArray();

        var grouped = inventoryItems
            .GroupBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (grouped.Length == 0)
        {
            _ = builder.AddOption(new TextMenuOption(Localize(player, "shop.menu.empty.inventory")) { Enabled = false });
            return builder.Build();
        }

        foreach (var categoryGroup in grouped)
        {
            var category = categoryGroup.Key;
            var count = categoryGroup.Count();
            var categoryButton = new ButtonMenuOption(Localize(player, "shop.menu.category.entry", category, count));
            categoryButton.Click += (sender, args) =>
            {
                var parentMenu = (sender as IMenuOption)?.Menu;
                Core.MenusAPI.OpenMenuForPlayer(args.Player, BuildInventoryItemsMenu(args.Player, category, parentMenu));
                return ValueTask.CompletedTask;
            };
            _ = builder.AddOption(categoryButton);
        }

        return builder.Build();
    }

    private IMenuAPI BuildInventoryItemsMenu(IPlayer player, string category, IMenuAPI? parent = null)
    {
        var builder = CreateBaseMenuBuilder(player, "shop.menu.inventory.category.title", parent, category);
        var items = shopApi.GetItemsByCategory(category)
            .Where(item => shopApi.IsItemEnabled(player, item.Id))
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (items.Length == 0)
        {
            _ = builder.AddOption(new TextMenuOption(Localize(player, "shop.menu.empty.category_inventory", category)) { Enabled = false });
            return builder.Build();
        }

        foreach (var item in items)
        {
            var button = new ButtonMenuOption(BuildInventoryItemText(player, item));
            button.Comment = BuildInventoryItemComment(player, item);
            button.Click += (sender, args) =>
            {
                var parentMenu = (sender as IMenuOption)?.Menu;
                Core.MenusAPI.OpenMenuForPlayer(args.Player, BuildInventoryItemActionMenu(args.Player, item, parentMenu));
                return ValueTask.CompletedTask;
            };
            _ = builder.AddOption(button);
        }

        return builder.Build();
    }

    private IMenuAPI BuildInventoryItemActionMenu(IPlayer player, ShopItemDefinition item, IMenuAPI? parent = null)
    {
        var builder = CreateBaseMenuBuilder(player, "shop.menu.inventory.item.title", parent, item.DisplayName);

        _ = builder.AddOption(new TextMenuOption(
            Localize(player, "shop.menu.inventory.item.info", item.DisplayName, item.Category))
        {
            Enabled = false
        });

        if (Settings.Behavior.AllowSelling && item.CanBeSold)
        {
            var sellAmount = item.SellPrice ?? Math.Round(item.Price * Settings.Behavior.DefaultSellRefundRatio, 0, MidpointRounding.AwayFromZero);
            var sellButton = new ButtonMenuOption(Localize(player, "shop.menu.inventory.item.sell", FormatCredits(sellAmount)));
            sellButton.Click += (sender, args) =>
            {
                _ = shopApi.SellItem(args.Player, item.Id);
                Core.MenusAPI.OpenMenuForPlayer(args.Player, BuildInventoryItemsMenu(args.Player, item.Category, parent));
                return ValueTask.CompletedTask;
            };
            _ = builder.AddOption(sellButton);
        }
        else
        {
            _ = builder.AddOption(new TextMenuOption(Localize(player, "shop.menu.inventory.item.not_sellable")) { Enabled = false });
        }

        return builder.Build();
    }

    private IMenuBuilderAPI CreateBaseMenuBuilder(IPlayer player, string titleKey, IMenuAPI? parent = null, params object[] args)
    {
        var builder = Core.MenusAPI
            .CreateBuilder()
            .EnableExit()
            .SetPlayerFrozen(Settings.Menus.FreezePlayerWhileOpen);

        if (Settings.Menus.EnableMenuSound)
        {
            _ = builder.EnableSound();
        }
        else
        {
            _ = builder.DisableSound();
        }

        _ = builder
            .Design.SetMenuTitle(Localize(player, titleKey, args))
            .Design.SetMaxVisibleItems(Settings.Menus.MaxVisibleItems)
            .Design.SetDefaultComment(Localize(player, Settings.Menus.DefaultCommentTranslationKey));

        if (parent is not null)
        {
            _ = builder.BindToParent(parent);
        }

        return builder;
    }

    private string BuildBuyItemText(IPlayer player, ShopItemDefinition item)
    {
        if (shopApi.IsItemEnabled(player, item.Id))
        {
            return Localize(player, "shop.menu.buy.item.owned", item.DisplayName);
        }

        return Localize(player, "shop.menu.buy.item.entry", item.DisplayName, FormatCredits(item.Price));
    }

    private string BuildBuyItemComment(IPlayer player, ShopItemDefinition item)
    {
        var durationText = item.Duration.HasValue
            ? Localize(player, "shop.menu.item.duration", (int)Math.Ceiling(item.Duration.Value.TotalSeconds))
            : Localize(player, "shop.menu.item.duration.permanent");

        return Localize(player, "shop.menu.buy.item.comment", item.Category, durationText);
    }

    private string BuildInventoryItemText(IPlayer player, ShopItemDefinition item)
    {
        var expireAt = shopApi.GetItemExpireAt(player, item.Id);
        if (!expireAt.HasValue)
        {
            return Localize(player, "shop.menu.inventory.item.entry", item.DisplayName, Localize(player, "shop.menu.item.duration.permanent"));
        }

        var remaining = expireAt.Value - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (remaining <= 0)
        {
            return Localize(player, "shop.menu.inventory.item.entry", item.DisplayName, Localize(player, "shop.menu.item.expired"));
        }

        return Localize(player, "shop.menu.inventory.item.entry", item.DisplayName, Localize(player, "shop.menu.item.remaining", remaining));
    }

    private string BuildInventoryItemComment(IPlayer player, ShopItemDefinition item)
    {
        if (!Settings.Behavior.AllowSelling || !item.CanBeSold)
        {
            return Localize(player, "shop.menu.inventory.item.not_sellable");
        }

        var sellAmount = item.SellPrice ?? Math.Round(item.Price * Settings.Behavior.DefaultSellRefundRatio, 0, MidpointRounding.AwayFromZero);
        return Localize(player, "shop.menu.inventory.item.sell_hint", FormatCredits(sellAmount));
    }

    private static string FormatCredits(decimal value)
    {
        if (value == decimal.Truncate(value))
        {
            return ((int)value).ToString();
        }

        return value.ToString("0.##");
    }
}
