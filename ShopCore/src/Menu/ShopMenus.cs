using ShopCore.Contract;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Menu;
using SwiftlyS2.Shared.Menu.Components;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;

namespace ShopCore;

public partial class ShopCore
{
    private const string MenuIdPrefix = "shopcore.";

    private readonly record struct InventoryItemSnapshot(ShopItemDefinition Item, long? ExpireAtUnixSeconds);

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

        _ = BuildBuyCategoryMenu(player).Open(player);
    }

    private void HandleOpenInventoryMenuCommand(ICommandContext context)
    {
        if (context.Sender is not IPlayer player || !player.IsValid)
        {
            context.Reply("This command is available only in-game.");
            return;
        }

        _ = BuildInventoryCategoryMenu(player).Open(player);
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

    private void OpenShopMainMenu(IPlayer player)
    {
        _ = BuildMainMenu(player).Open(player);
    }

    private IMenu BuildMainMenu(IPlayer player)
    {
        var builder = CreateBaseMenuBuilder(player, "main", "shop.menu.main.title");
        var credits = shopApi.GetCredits(player);

        _ = builder.Add(new TextComponent(
            Localize(player, "shop.menu.main.credits", FormatCredits(credits)))
        {
            Enabled = false
        });

        _ = builder.Add(new SubmenuComponent(
            Localize(player, "shop.menu.main.buy"),
            session => BuildBuyCategoryMenu(session.Player)));

        _ = builder.Add(new SubmenuComponent(
            Localize(player, "shop.menu.main.inventory"),
            session => BuildInventoryCategoryMenu(session.Player)));

        return BuildMenu(player, builder);
    }

    private IMenu BuildBuyCategoryMenu(IPlayer player)
    {
        var builder = CreateBaseMenuBuilder(player, "buy.categories", "shop.menu.buy.title");
        var items = shopApi.GetItems()
            .Where(item => shopApi.IsItemVisibleToPlayer(player, item))
            .ToArray();

        var grouped = items
            .GroupBy(item => ParseCategoryPath(item.Category).Category, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (grouped.Length == 0)
        {
            _ = builder.Add(new TextComponent(Localize(player, "shop.menu.empty.buy")) { Enabled = false });
            return BuildMenu(player, builder);
        }

        foreach (var categoryGroup in grouped)
        {
            var category = categoryGroup.Key;
            var displayCategory = LocalizeCategorySegment(player, category);
            var count = categoryGroup.Count();
            _ = builder.Add(CategoryEntry(
                Localize(player, "shop.menu.category.entry", displayCategory, count),
                session => BuildBuySubcategoryOrItemsMenu(session.Player, category),
                categoryGroup));
        }

        return BuildMenu(player, builder);
    }

    private IMenu BuildBuySubcategoryOrItemsMenu(IPlayer player, string category)
    {
        var items = shopApi.GetItems()
            .Where(item => shopApi.IsItemVisibleToPlayer(player, item) && CategoryMatches(item, category, null))
            .ToArray();

        if (items.Length == 0)
        {
            var localizedCategory = LocalizeCategorySegment(player, category);
            var emptyBuilder = CreateBaseMenuBuilder(player, "buy.subcategories", "shop.menu.buy.category.title", localizedCategory);
            _ = emptyBuilder.Add(new TextComponent(Localize(player, "shop.menu.empty.category", localizedCategory)) { Enabled = false });
            return BuildMenu(player, emptyBuilder);
        }

        var grouped = items
            .GroupBy(item => ParseCategoryPath(item.Category).Subcategory ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var hasSubcategories = grouped.Any(group => !string.IsNullOrWhiteSpace(group.Key));
        if (!hasSubcategories)
        {
            return BuildBuyItemsMenu(player, category);
        }

        var localizedCategoryTitle = LocalizeCategorySegment(player, category);
        var builder = CreateBaseMenuBuilder(player, "buy.subcategories", "shop.menu.buy.category.title", localizedCategoryTitle);
        foreach (var subgroup in grouped)
        {
            var subcategory = string.IsNullOrWhiteSpace(subgroup.Key) ? null : subgroup.Key;
            var displayName = subcategory is null
                ? Localize(player, "shop.menu.subcategory.general")
                : LocalizeSubcategorySegment(player, subcategory);
            var count = subgroup.Count();
            _ = builder.Add(CategoryEntry(
                Localize(player, "shop.menu.category.entry", displayName, count),
                session => BuildBuyItemsMenu(session.Player, category, subcategory),
                subgroup));
        }

        return BuildMenu(player, builder);
    }

    private IMenu BuildBuyItemsMenu(IPlayer player, string category, string? subcategory = null)
    {
        var categoryPathText = BuildLocalizedCategoryPathText(player, category, subcategory);
        var builder = CreateBaseMenuBuilder(player, "buy.items", "shop.menu.buy.category.title", categoryPathText);
        var items = shopApi.GetItems()
            .Where(item => shopApi.IsItemVisibleToPlayer(player, item))
            .Where(item => CategoryMatches(item, category, subcategory))
            .OrderBy(item => item.Price)
            .ThenBy(item => shopApi.GetItemDisplayName(player, item), StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (items.Length == 0)
        {
            _ = builder.Add(new TextComponent(Localize(player, "shop.menu.empty.category", categoryPathText)) { Enabled = false });
            return BuildMenu(player, builder);
        }

        foreach (var item in items)
        {
            _ = builder.Add(new SubmenuComponent(
                BuildBuyItemText(player, item),
                session => BuildBuyItemActionMenu(session.Player, item, category, subcategory))
            {
                Comment = BuildBuyItemComment(player, item),
                // A row's Tag is the item it stands for, so any renderer can identify it.
                Tag = item
            });
        }

        return BuildMenu(player, builder);
    }

    private IMenu BuildBuyItemActionMenu(
        IPlayer player,
        ShopItemDefinition item,
        string category,
        string? subcategory)
    {
        var builder = CreateBaseMenuBuilder(player, "buy.item", "shop.menu.buy.item.title", shopApi.GetItemDisplayName(player, item));

        _ = builder.Add(new TextComponent(BuildBuyItemText(player, item))
        {
            Enabled = false,
            Comment = BuildBuyItemComment(player, item)
        });

        if (item.AllowPreview)
        {
            _ = builder.Add(new ButtonComponent(Localize(player, "shop.menu.buy.item.preview"), ctx =>
            {
                _ = shopApi.PreviewItem(ctx.Player, item.Id);
                return ValueTask.CompletedTask;
            }));
        }

        _ = builder.Add(new ButtonComponent(Localize(player, "shop.menu.buy.item.buy", FormatCredits(item.Price)), ctx =>
        {
            _ = shopApi.PurchaseItem(ctx.Player, item.Id);
            OpenInPlaceOfParent(ctx, BuildBuyItemsMenu(ctx.Player, category, subcategory));
            return ValueTask.CompletedTask;
        }));

        var menu = BuildMenu(player, builder);
        // A page's Tag is the item it stands for, so any renderer can identify it.
        menu.Tag = item;
        return menu;
    }

    private IMenu BuildInventoryCategoryMenu(IPlayer player)
    {
        var builder = CreateBaseMenuBuilder(player, "inventory.categories", "shop.menu.inventory.title");
        var inventorySnapshot = BuildInventorySnapshot(player);

        var grouped = inventorySnapshot
            .GroupBy(entry => ParseCategoryPath(entry.Item.Category).Category, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (grouped.Length == 0)
        {
            _ = builder.Add(new TextComponent(Localize(player, "shop.menu.empty.inventory")) { Enabled = false });
            return BuildMenu(player, builder);
        }

        foreach (var categoryGroup in grouped)
        {
            var category = categoryGroup.Key;
            var displayCategory = LocalizeCategorySegment(player, category);
            var count = categoryGroup.Count();
            _ = builder.Add(CategoryEntry(
                Localize(player, "shop.menu.category.entry", displayCategory, count),
                session => BuildInventorySubcategoryOrItemsMenu(session.Player, category, inventorySnapshot),
                categoryGroup.Select(entry => entry.Item)));
        }

        return BuildMenu(player, builder);
    }

    private IMenu BuildInventorySubcategoryOrItemsMenu(
        IPlayer player,
        string category,
        IReadOnlyCollection<InventoryItemSnapshot>? inventorySnapshot = null)
    {
        var snapshot = inventorySnapshot ?? BuildInventorySnapshot(player);
        var items = snapshot
            .Where(entry => CategoryMatches(entry.Item, category, null))
            .ToArray();

        if (items.Length == 0)
        {
            var localizedCategory = LocalizeCategorySegment(player, category);
            var emptyBuilder = CreateBaseMenuBuilder(player, "inventory.subcategories", "shop.menu.inventory.category.title", localizedCategory);
            _ = emptyBuilder.Add(new TextComponent(Localize(player, "shop.menu.empty.category_inventory", localizedCategory)) { Enabled = false });
            return BuildMenu(player, emptyBuilder);
        }

        var grouped = items
            .GroupBy(entry => ParseCategoryPath(entry.Item.Category).Subcategory ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var hasSubcategories = grouped.Any(group => !string.IsNullOrWhiteSpace(group.Key));
        if (!hasSubcategories)
        {
            return BuildInventoryItemsMenu(player, category, null, snapshot);
        }

        var localizedCategoryTitle = LocalizeCategorySegment(player, category);
        var builder = CreateBaseMenuBuilder(player, "inventory.subcategories", "shop.menu.inventory.category.title", localizedCategoryTitle);
        foreach (var subgroup in grouped)
        {
            var subcategory = string.IsNullOrWhiteSpace(subgroup.Key) ? null : subgroup.Key;
            var displayName = subcategory is null
                ? Localize(player, "shop.menu.subcategory.general")
                : LocalizeSubcategorySegment(player, subcategory);
            var count = subgroup.Count();
            _ = builder.Add(CategoryEntry(
                Localize(player, "shop.menu.category.entry", displayName, count),
                session => BuildInventoryItemsMenu(session.Player, category, subcategory, snapshot),
                subgroup.Select(entry => entry.Item)));
        }

        return BuildMenu(player, builder);
    }

    private IMenu BuildInventoryItemsMenu(
        IPlayer player,
        string category,
        string? subcategory = null,
        IReadOnlyCollection<InventoryItemSnapshot>? inventorySnapshot = null)
    {
        var categoryPathText = BuildLocalizedCategoryPathText(player, category, subcategory);
        var builder = CreateBaseMenuBuilder(player, "inventory.items", "shop.menu.inventory.category.title", categoryPathText);
        var snapshot = inventorySnapshot ?? BuildInventorySnapshot(player);
        var items = snapshot
            .Where(entry => CategoryMatches(entry.Item, category, subcategory))
            .OrderBy(entry => shopApi.GetItemDisplayName(player, entry.Item), StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (items.Length == 0)
        {
            _ = builder.Add(new TextComponent(Localize(player, "shop.menu.empty.category_inventory", categoryPathText)) { Enabled = false });
            return BuildMenu(player, builder);
        }

        foreach (var entry in items)
        {
            var item = entry.Item;
            _ = builder.Add(new SubmenuComponent(
                BuildInventoryItemText(player, item),
                session => BuildInventoryItemActionMenu(session.Player, item, category, subcategory))
            {
                Comment = BuildInventoryItemComment(player, item, entry.ExpireAtUnixSeconds),
                Overflow = MenuTextOverflow.None,
                // A row's Tag is the item it stands for, so any renderer can identify it.
                Tag = item
            });
        }

        return BuildMenu(player, builder);
    }

    private IMenu BuildInventoryItemActionMenu(
        IPlayer player,
        ShopItemDefinition item,
        string category,
        string? subcategory)
    {
        var builder = CreateBaseMenuBuilder(player, "inventory.item", "shop.menu.inventory.item.title", shopApi.GetItemDisplayName(player, item));
        var isEnabled = item.IsEquipable && shopApi.IsItemEnabled(player, item.Id);
        var expireAt = item.Duration.HasValue ? shopApi.GetItemExpireAt(player, item.Id) : null;

        _ = builder.Add(new TextComponent(BuildInventoryItemInfoText(player, isEnabled))
        {
            Enabled = false
        });

        _ = builder.Add(new CountdownTextComponent
        {
            Enabled = false,
            Overflow = MenuTextOverflow.None,
            TextProvider = session => BuildInventoryItemDurationInfoText(session.Player, item, expireAt)
        });

        // Rebuilt rather than updated in place: IsItemEnabled expires the item as a side effect, so it must not run per frame.
        _ = builder.Add(new ButtonComponent(
            isEnabled
                ? Localize(player, "shop.menu.inventory.item.toggle.disable")
                : Localize(player, "shop.menu.inventory.item.toggle.enable"),
            ctx =>
            {
                var p = ctx.Player;
                if (p is null || !p.IsValid) return ValueTask.CompletedTask;

                var currentState = shopApi.IsItemEnabled(p, item.Id);
                _ = shopApi.SetItemEnabled(p, item.Id, !currentState);

                var focusedIndex = ctx.Session.FocusedIndex;
                var parent = ctx.Menu.Parent;
                var refreshed = BuildInventoryItemActionMenu(p, item, category, subcategory);
                refreshed.Parent = parent;
                _ = refreshed.Open(p).SetFocus(focusedIndex);

                return ValueTask.CompletedTask;
            }));

        if (Settings.Behavior.AllowSelling && item.CanBeSold)
        {
            var sellAmount = shopApi.GetSellPrice(item);
            _ = builder.Add(new ButtonComponent(Localize(player, "shop.menu.inventory.item.sell", FormatCredits(sellAmount)), ctx =>
            {
                var p = ctx.Player;
                if (p is null || !p.IsValid) return ValueTask.CompletedTask;

                _ = shopApi.SellItem(p, item.Id);
                OpenInPlaceOfParent(ctx, BuildInventoryItemsMenu(p, category, subcategory));
                return ValueTask.CompletedTask;
            }));
        }
        else
        {
            _ = builder.Add(new TextComponent(Localize(player, "shop.menu.inventory.item.not_sellable")) { Enabled = false });
        }

        var menu = BuildMenu(player, builder);
        // A page's Tag is the item it stands for, so any renderer can identify it.
        menu.Tag = item;
        return menu;
    }

    // A category row's Tag is the items it opens, so any renderer can describe it.
    private static SubmenuComponent CategoryEntry(string text, Func<IMenuSession, IMenu?> open, IEnumerable<ShopItemDefinition> items)
    {
        return new SubmenuComponent(text, open) { Tag = items.ToArray() };
    }

    private IMenuBuilder CreateBaseMenuBuilder(IPlayer player, string menuId, string titleKey, params object[] args)
    {
        return Core.Menu
            .CreateMenu(MenuIdPrefix + menuId)
            .AddHeader(new TitleComponent(Localize(player, titleKey, args)) { Overflow = MenuTextOverflow.None })
            .WithItemsPerPage(Settings.Menus.MaxVisibleItems);
    }

    private IMenu BuildMenu(IPlayer player, IMenuBuilder builder)
    {
        var menu = builder.Build();
        var defaultComment = Localize(player, Settings.Menus.DefaultCommentTranslationKey);

        foreach (var component in menu.GetComponents(MenuRegion.Body).OfType<MenuComponentBase>())
        {
            if (string.IsNullOrWhiteSpace(component.Comment))
            {
                component.Comment = defaultComment;
            }

            // Only the select sound is per component; scroll/exit sounds come from SwiftlyS2's menus.jsonc.
            if (!Settings.Menus.EnableMenuSound)
            {
                component.PlaySound = false;
            }
        }

        // The old menu's Closed fires before the next menu's Opened, so navigating keeps the player frozen.
        if (Settings.Menus.FreezePlayerWhileOpen)
        {
            menu.Opened += session => SetMenuFreeze(session.Player, true);
            menu.Closed += session => SetMenuFreeze(session.Player, false);
        }

        return menu;
    }

    private static void OpenInPlaceOfParent(MenuActionContext ctx, IMenu refreshed)
    {
        refreshed.Parent = ctx.Menu.Parent?.Parent;
        _ = refreshed.Open(ctx.Player);
    }

    private void SetMenuFreeze(IPlayer player, bool frozen)
    {
        Core.Scheduler.NextTick(() => ApplyMenuFreeze(player, frozen));
    }

    // Closed queues its unfreeze on Core.Scheduler, which unloading cancels before the next tick.
    private void ReleaseMenuFreezes()
    {
        if (!Settings.Menus.FreezePlayerWhileOpen || !Core.IsGameThread)
        {
            return;
        }

        foreach (var player in Core.PlayerManager.GetAllValidPlayers())
        {
            if (Core.Menu.GetSession(player)?.Menu.Id.StartsWith(MenuIdPrefix, StringComparison.Ordinal) == true)
            {
                ApplyMenuFreeze(player, false);
            }
        }
    }

    private static void ApplyMenuFreeze(IPlayer player, bool frozen)
    {
        if (!player.IsValid || player.PlayerPawn is not { IsValid: true } pawn)
        {
            return;
        }

        var moveType = frozen ? MoveType_t.MOVETYPE_NONE : MoveType_t.MOVETYPE_WALK;
        if (pawn.MoveType == moveType && pawn.ActualMoveType == moveType)
        {
            return;
        }

        pawn.MoveType = moveType;
        pawn.ActualMoveType = moveType;
        pawn.MoveTypeUpdated();
    }

    private string BuildBuyItemText(IPlayer player, ShopItemDefinition item)
    {
        return Localize(player, "shop.menu.buy.item.entry", shopApi.GetItemDisplayName(player, item), FormatCredits(item.Price));
    }

    private string BuildBuyItemComment(IPlayer player, ShopItemDefinition item)
    {
        return Localize(player, "shop.menu.buy.item.comment", GetCurrentDurationText(player, item));
    }

    private string BuildInventoryItemText(IPlayer player, ShopItemDefinition item)
    {
        return Localize(player, "shop.menu.inventory.item.entry", shopApi.GetItemDisplayName(player, item));
    }

    private string BuildInventoryItemComment(IPlayer player, ShopItemDefinition item)
    {
        return BuildInventoryItemComment(player, item, null);
    }

    private string BuildInventoryItemComment(IPlayer player, ShopItemDefinition item, long? expireAtUnixSeconds)
    {
        return Localize(player, "shop.menu.inventory.item.comment", GetCurrentDurationText(player, item, expireAtUnixSeconds));
    }

    private string BuildInventoryItemInfoText(IPlayer player, bool enabled)
    {
        var stateText = enabled
            ? Localize(player, "shop.menu.item.state.enabled")
            : Localize(player, "shop.menu.item.state.disabled");
        return Localize(player, "shop.menu.inventory.item.info", stateText);
    }

    private string BuildInventoryItemDurationInfoText(IPlayer player, ShopItemDefinition item, long? expireAtUnixSeconds)
    {
        return Localize(player, "shop.menu.inventory.item.duration_info", GetCurrentDurationText(player, item, expireAtUnixSeconds));
    }

    private string GetCurrentDurationText(IPlayer player, ShopItemDefinition item)
    {
        var expireAt = item.Duration.HasValue ? shopApi.GetItemExpireAt(player, item.Id) : null;
        return GetCurrentDurationText(player, item, expireAt);
    }

    private string GetCurrentDurationText(IPlayer player, ShopItemDefinition item, long? expireAtUnixSeconds)
    {
        if (!item.Duration.HasValue)
        {
            return Localize(player, "shop.menu.item.duration.permanent");
        }

        if (!expireAtUnixSeconds.HasValue)
        {
            return FormatDurationWords((long)Math.Ceiling(item.Duration.Value.TotalSeconds));
        }

        var remaining = expireAtUnixSeconds.Value - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (remaining <= 0)
        {
            return Localize(player, "shop.menu.item.expired");
        }

        return FormatDurationWords(remaining);
    }

    private IReadOnlyList<InventoryItemSnapshot> BuildInventorySnapshot(IPlayer player)
    {
        var snapshots = new List<InventoryItemSnapshot>();
        foreach (var item in shopApi.GetItems())
        {
            if (!shopApi.IsItemOwned(player, item.Id))
            {
                continue;
            }

            var expireAt = item.Duration.HasValue ? shopApi.GetItemExpireAt(player, item.Id) : null;
            snapshots.Add(new InventoryItemSnapshot(item, expireAt));
        }

        return snapshots;
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

    private string BuildLocalizedCategoryPathText(IPlayer player, string category, string? subcategory)
    {
        var localizedCategory = LocalizeCategorySegment(player, category);
        if (string.IsNullOrWhiteSpace(subcategory))
        {
            return localizedCategory;
        }

        var localizedSubcategory = LocalizeSubcategorySegment(player, subcategory);
        return $"{localizedCategory} > {localizedSubcategory}";
    }

    private string LocalizeCategorySegment(IPlayer player, string segment)
    {
        return LocalizeCategoryLikeSegment(player, segment, "shop.menu.category");
    }

    private string LocalizeSubcategorySegment(IPlayer player, string segment)
    {
        return LocalizeCategoryLikeSegment(player, segment, "shop.menu.subcategory");
    }

    private string LocalizeCategoryLikeSegment(IPlayer player, string segment, string keyPrefix)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            return segment;
        }

        var key = $"{keyPrefix}.{ToTranslationSlug(segment)}";
        var localized = Localize(player, key);
        return IsMissingTranslation(localized, key) ? segment : localized;
    }

    // SwiftlyS2 answers a key that is in neither the player's file nor en.jsonc with "{language}.{key}".
    private static bool IsMissingTranslation(string localized, string key)
    {
        if (string.Equals(localized, key, StringComparison.Ordinal))
        {
            return true;
        }

        var dot = localized.Length - key.Length - 1;
        return dot > 0
            && localized[dot] == '.'
            && localized.EndsWith(key, StringComparison.Ordinal)
            && localized.IndexOf('.') == dot;
    }

    private static string ToTranslationSlug(string input)
    {
        var chars = input
            .Trim()
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray();

        var collapsed = new string(chars);
        while (collapsed.Contains("__", StringComparison.Ordinal))
        {
            collapsed = collapsed.Replace("__", "_", StringComparison.Ordinal);
        }

        return collapsed.Trim('_');
    }

    // TextComponent only redraws on input, which would freeze a countdown between key presses.
    private sealed class CountdownTextComponent : TextComponent
    {
        public override bool NeedsRedraw(IMenuSession session, DateTime now)
        {
            if (base.NeedsRedraw(session, now))
            {
                return true;
            }

            // The chat renderer re-posts the whole menu on every redraw.
            if (string.Equals(session.Menu.Renderer.Id, MenuRendererIds.Chat, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var state = session.GetState<CountdownState>(this);
            var second = now.Ticks / TimeSpan.TicksPerSecond;
            if (state.CheckedSecond == second)
            {
                return false;
            }

            state.CheckedSecond = second;
            return !string.Equals(RawText(session), state.RenderedText, StringComparison.Ordinal);
        }

        public override MenuNode Render(IMenuComponentRenderContext context)
        {
            context.Session.GetState<CountdownState>(this).RenderedText = RawText(context.Session);
            return base.Render(context);
        }

        private sealed class CountdownState
        {
            public long CheckedSecond { get; set; }

            public string? RenderedText { get; set; }
        }
    }
}
