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
            .GroupBy(item => ParseCategoryPath(item.Category).Category, StringComparer.OrdinalIgnoreCase)
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
                Core.MenusAPI.OpenMenuForPlayer(args.Player, BuildBuySubcategoryOrItemsMenu(args.Player, category, parentMenu));
                return ValueTask.CompletedTask;
            };
            _ = builder.AddOption(categoryButton);
        }

        return builder.Build();
    }

    private IMenuAPI BuildBuySubcategoryOrItemsMenu(IPlayer player, string category, IMenuAPI? parent = null)
    {
        var items = shopApi.GetItems()
            .Where(item => item.Enabled && CategoryMatches(item, category, null))
            .ToArray();

        if (items.Length == 0)
        {
            var emptyBuilder = CreateBaseMenuBuilder(player, "shop.menu.buy.category.title", parent, category);
            _ = emptyBuilder.AddOption(new TextMenuOption(Localize(player, "shop.menu.empty.category", category)) { Enabled = false });
            return emptyBuilder.Build();
        }

        var grouped = items
            .GroupBy(item => ParseCategoryPath(item.Category).Subcategory ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var hasSubcategories = grouped.Any(group => !string.IsNullOrWhiteSpace(group.Key));
        if (!hasSubcategories)
        {
            return BuildBuyItemsMenu(player, category, null, parent);
        }

        var builder = CreateBaseMenuBuilder(player, "shop.menu.buy.category.title", parent, category);
        foreach (var subgroup in grouped)
        {
            var subcategory = string.IsNullOrWhiteSpace(subgroup.Key) ? null : subgroup.Key;
            var displayName = subcategory ?? Localize(player, "shop.menu.subcategory.general");
            var count = subgroup.Count();
            var button = new ButtonMenuOption(Localize(player, "shop.menu.category.entry", displayName, count));
            button.Click += (sender, args) =>
            {
                var parentMenu = (sender as IMenuOption)?.Menu;
                Core.MenusAPI.OpenMenuForPlayer(args.Player, BuildBuyItemsMenu(args.Player, category, subcategory, parentMenu));
                return ValueTask.CompletedTask;
            };
            _ = builder.AddOption(button);
        }

        return builder.Build();
    }

    private IMenuAPI BuildBuyItemsMenu(IPlayer player, string category, string? subcategory = null, IMenuAPI? parent = null)
    {
        var categoryPathText = BuildCategoryPathText(category, subcategory);
        var builder = CreateBaseMenuBuilder(player, "shop.menu.buy.category.title", parent, categoryPathText);
        var items = shopApi.GetItems()
            .Where(item => item.Enabled)
            .Where(item => CategoryMatches(item, category, subcategory))
            .OrderBy(item => item.Price)
            .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (items.Length == 0)
        {
            _ = builder.AddOption(new TextMenuOption(Localize(player, "shop.menu.empty.category", categoryPathText)) { Enabled = false });
            return builder.Build();
        }

        foreach (var item in items)
        {
            var itemButton = new ButtonMenuOption(BuildBuyItemText(player, item));
            itemButton.Comment = BuildBuyItemComment(player, item);
            itemButton.Click += (sender, args) =>
            {
                var parentMenu = (sender as IMenuOption)?.Menu;
                Core.MenusAPI.OpenMenuForPlayer(args.Player, BuildBuyItemActionMenu(args.Player, item, category, subcategory, parentMenu));
                return ValueTask.CompletedTask;
            };
            _ = builder.AddOption(itemButton);
        }

        return builder.Build();
    }

    private IMenuAPI BuildBuyItemActionMenu(
        IPlayer player,
        ShopItemDefinition item,
        string category,
        string? subcategory = null,
        IMenuAPI? parent = null)
    {
        var builder = CreateBaseMenuBuilder(player, "shop.menu.buy.item.title", parent, item.DisplayName);

        var infoOption = new TextMenuOption(BuildBuyItemText(player, item))
        {
            Enabled = false,
            Comment = BuildBuyItemComment(player, item)
        };
        _ = builder.AddOption(infoOption);

        if (item.AllowPreview)
        {
            var previewButton = new ButtonMenuOption(Localize(player, "shop.menu.buy.item.preview"));
            previewButton.Click += (sender, args) =>
            {
                _ = shopApi.PreviewItem(args.Player, item.Id);
                var currentMenu = (sender as IMenuOption)?.Menu;
                var parentMenu = currentMenu?.Parent.ParentMenu ?? parent;
                Core.MenusAPI.OpenMenuForPlayer(args.Player, BuildBuyItemActionMenu(args.Player, item, category, subcategory, parentMenu));
                return ValueTask.CompletedTask;
            };
            _ = builder.AddOption(previewButton);
        }

        var buyButton = new ButtonMenuOption(Localize(player, "shop.menu.buy.item.buy", FormatCredits(item.Price)));
        buyButton.Click += (sender, args) =>
        {
            _ = shopApi.PurchaseItem(args.Player, item.Id);
            var listParentMenu = parent?.Parent.ParentMenu;
            Core.MenusAPI.OpenMenuForPlayer(args.Player, BuildBuyItemsMenu(args.Player, category, subcategory, listParentMenu));
            return ValueTask.CompletedTask;
        };
        _ = builder.AddOption(buyButton);

        return builder.Build();
    }

    private IMenuAPI BuildInventoryCategoryMenu(IPlayer player, IMenuAPI? parent = null)
    {
        var builder = CreateBaseMenuBuilder(player, "shop.menu.inventory.title", parent);
        var inventoryItems = shopApi.GetItems()
            .Where(item => shopApi.IsItemOwned(player, item.Id))
            .ToArray();

        var grouped = inventoryItems
            .GroupBy(item => ParseCategoryPath(item.Category).Category, StringComparer.OrdinalIgnoreCase)
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
                Core.MenusAPI.OpenMenuForPlayer(args.Player, BuildInventorySubcategoryOrItemsMenu(args.Player, category, parentMenu));
                return ValueTask.CompletedTask;
            };
            _ = builder.AddOption(categoryButton);
        }

        return builder.Build();
    }

    private IMenuAPI BuildInventorySubcategoryOrItemsMenu(IPlayer player, string category, IMenuAPI? parent = null)
    {
        var items = shopApi.GetItems()
            .Where(item => shopApi.IsItemOwned(player, item.Id))
            .Where(item => CategoryMatches(item, category, null))
            .ToArray();

        if (items.Length == 0)
        {
            var emptyBuilder = CreateBaseMenuBuilder(player, "shop.menu.inventory.category.title", parent, category);
            _ = emptyBuilder.AddOption(new TextMenuOption(Localize(player, "shop.menu.empty.category_inventory", category)) { Enabled = false });
            return emptyBuilder.Build();
        }

        var grouped = items
            .GroupBy(item => ParseCategoryPath(item.Category).Subcategory ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var hasSubcategories = grouped.Any(group => !string.IsNullOrWhiteSpace(group.Key));
        if (!hasSubcategories)
        {
            return BuildInventoryItemsMenu(player, category, null, parent);
        }

        var builder = CreateBaseMenuBuilder(player, "shop.menu.inventory.category.title", parent, category);
        foreach (var subgroup in grouped)
        {
            var subcategory = string.IsNullOrWhiteSpace(subgroup.Key) ? null : subgroup.Key;
            var displayName = subcategory ?? Localize(player, "shop.menu.subcategory.general");
            var count = subgroup.Count();
            var button = new ButtonMenuOption(Localize(player, "shop.menu.category.entry", displayName, count));
            button.Click += (sender, args) =>
            {
                var parentMenu = (sender as IMenuOption)?.Menu;
                Core.MenusAPI.OpenMenuForPlayer(args.Player, BuildInventoryItemsMenu(args.Player, category, subcategory, parentMenu));
                return ValueTask.CompletedTask;
            };
            _ = builder.AddOption(button);
        }

        return builder.Build();
    }

    private IMenuAPI BuildInventoryItemsMenu(IPlayer player, string category, string? subcategory = null, IMenuAPI? parent = null)
    {
        var categoryPathText = BuildCategoryPathText(category, subcategory);
        var builder = CreateBaseMenuBuilder(player, "shop.menu.inventory.category.title", parent, categoryPathText);
        var items = shopApi.GetItems()
            .Where(item => shopApi.IsItemOwned(player, item.Id))
            .Where(item => CategoryMatches(item, category, subcategory))
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (items.Length == 0)
        {
            _ = builder.AddOption(new TextMenuOption(Localize(player, "shop.menu.empty.category_inventory", categoryPathText)) { Enabled = false });
            return builder.Build();
        }

        foreach (var item in items)
        {
            var button = new ButtonMenuOption(BuildInventoryItemText(player, item));
            button.Comment = BuildInventoryItemComment(player, item);
            button.BeforeFormat += (sender, args) =>
            {
                args.CustomText = BuildInventoryItemText(args.Player, item);
            };
            button.Click += (sender, args) =>
            {
                var parentMenu = (sender as IMenuOption)?.Menu;
                Core.MenusAPI.OpenMenuForPlayer(args.Player, BuildInventoryItemActionMenu(args.Player, item, category, subcategory, parentMenu));
                return ValueTask.CompletedTask;
            };
            _ = builder.AddOption(button);
        }

        return builder.Build();
    }

    private IMenuAPI BuildInventoryItemActionMenu(
        IPlayer player,
        ShopItemDefinition item,
        string category,
        string? subcategory = null,
        IMenuAPI? parent = null)
    {
        var builder = CreateBaseMenuBuilder(player, "shop.menu.inventory.item.title", parent, item.DisplayName);

        var infoOption = new TextMenuOption(BuildInventoryItemInfoText(player, item))
        {
            Enabled = false
        };
        infoOption.BeforeFormat += (sender, args) =>
        {
            args.CustomText = BuildInventoryItemInfoText(args.Player, item);
        };
        _ = builder.AddOption(infoOption);

        var durationInfoOption = new TextMenuOption(BuildInventoryItemDurationInfoText(player, item))
        {
            Enabled = false
        };
        durationInfoOption.BeforeFormat += (sender, args) =>
        {
            args.CustomText = BuildInventoryItemDurationInfoText(args.Player, item);
        };
        _ = builder.AddOption(durationInfoOption);

        var toggleButton = new ButtonMenuOption(
            shopApi.IsItemEnabled(player, item.Id)
                ? Localize(player, "shop.menu.inventory.item.toggle.disable")
                : Localize(player, "shop.menu.inventory.item.toggle.enable")
        );
        toggleButton.BeforeFormat += (sender, args) =>
        {
            args.CustomText = shopApi.IsItemEnabled(args.Player, item.Id)
                ? Localize(args.Player, "shop.menu.inventory.item.toggle.disable")
                : Localize(args.Player, "shop.menu.inventory.item.toggle.enable");
        };
        toggleButton.Click += (sender, args) =>
        {
            var nextEnabled = !shopApi.IsItemEnabled(args.Player, item.Id);
            _ = shopApi.SetItemEnabled(args.Player, item.Id, nextEnabled);
            var parentMenu = (sender as IMenuOption)?.Menu;
            Core.MenusAPI.OpenMenuForPlayer(args.Player, BuildInventoryItemActionMenu(args.Player, item, category, subcategory, parentMenu));
            return ValueTask.CompletedTask;
        };
        _ = builder.AddOption(toggleButton);

        if (Settings.Behavior.AllowSelling && item.CanBeSold)
        {
            var sellAmount = item.SellPrice ?? Math.Round(item.Price * Settings.Behavior.DefaultSellRefundRatio, 0, MidpointRounding.AwayFromZero);
            var sellButton = new ButtonMenuOption(Localize(player, "shop.menu.inventory.item.sell", FormatCredits(sellAmount)));
            sellButton.Click += (sender, args) =>
            {
                _ = shopApi.SellItem(args.Player, item.Id);
                Core.MenusAPI.OpenMenuForPlayer(args.Player, BuildInventoryItemsMenu(args.Player, category, subcategory, parent));
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
        return Localize(player, "shop.menu.buy.item.entry", item.DisplayName, FormatCredits(item.Price));
    }

    private string BuildBuyItemComment(IPlayer player, ShopItemDefinition item)
    {
        return Localize(player, "shop.menu.buy.item.comment", GetCurrentDurationText(player, item));
    }

    private string BuildInventoryItemText(IPlayer player, ShopItemDefinition item)
    {
        return Localize(player, "shop.menu.inventory.item.entry", item.DisplayName);
    }

    private string BuildInventoryItemComment(IPlayer player, ShopItemDefinition item)
    {
        return Localize(player, "shop.menu.inventory.item.comment", GetCurrentDurationText(player, item));
    }

    private string BuildInventoryItemInfoText(IPlayer player, ShopItemDefinition item)
    {
        var stateText = shopApi.IsItemEnabled(player, item.Id)
            ? Localize(player, "shop.menu.item.state.enabled")
            : Localize(player, "shop.menu.item.state.disabled");
        return Localize(player, "shop.menu.inventory.item.info", stateText);
    }

    private string BuildInventoryItemDurationInfoText(IPlayer player, ShopItemDefinition item)
    {
        return Localize(player, "shop.menu.inventory.item.duration_info", GetCurrentDurationText(player, item));
    }

    private string GetCurrentDurationText(IPlayer player, ShopItemDefinition item)
    {
        if (!item.Duration.HasValue)
        {
            return Localize(player, "shop.menu.item.duration.permanent");
        }

        var expireAt = shopApi.GetItemExpireAt(player, item.Id);
        if (!expireAt.HasValue)
        {
            return FormatDurationWords((long)Math.Ceiling(item.Duration.Value.TotalSeconds));
        }

        var remaining = expireAt.Value - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (remaining <= 0)
        {
            return Localize(player, "shop.menu.item.expired");
        }

        return FormatDurationWords(remaining);
    }

    private static string FormatDurationWords(long totalSeconds)
    {
        if (totalSeconds < 0)
        {
            totalSeconds = 0;
        }

        var hours = totalSeconds / 3600;
        var minutes = (totalSeconds % 3600) / 60;
        var seconds = totalSeconds % 60;

        var hourWord = hours == 1 ? "Hour" : "Hours";
        var minuteWord = minutes == 1 ? "Minute" : "Minutes";
        var secondWord = seconds == 1 ? "Second" : "Seconds";

        return $"{hours} {hourWord} {minutes} {minuteWord} {seconds} {secondWord}";
    }

    private static string FormatCredits(decimal value)
    {
        if (value == decimal.Truncate(value))
        {
            return ((int)value).ToString();
        }

        return value.ToString("0.##");
    }

    private static (string Category, string? Subcategory) ParseCategoryPath(string categoryPath)
    {
        if (string.IsNullOrWhiteSpace(categoryPath))
        {
            return ("Misc", null);
        }

        var segments = categoryPath
            .Split(['/', '>'], StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => segment.Trim())
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .ToArray();

        if (segments.Length == 0)
        {
            return ("Misc", null);
        }

        if (segments.Length == 1)
        {
            return (segments[0], null);
        }

        return (segments[0], string.Join(" / ", segments.Skip(1)));
    }

    private static bool CategoryMatches(ShopItemDefinition item, string category, string? subcategory)
    {
        var parsed = ParseCategoryPath(item.Category);
        if (!string.Equals(parsed.Category, category, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(subcategory))
        {
            // Category-only match should include both direct-category and subcategory items.
            return true;
        }

        return string.Equals(parsed.Subcategory, subcategory, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildCategoryPathText(string category, string? subcategory)
    {
        if (string.IsNullOrWhiteSpace(subcategory))
        {
            return category;
        }

        return $"{category} > {subcategory}";
    }
}
