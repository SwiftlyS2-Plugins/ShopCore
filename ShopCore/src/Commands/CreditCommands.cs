using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Players;

namespace ShopCore;

public partial class ShopCore
{
    private void HandleGiftCreditsCommand(ICommandContext context)
    {
        if (context.Sender is not IPlayer sender || !sender.IsValid)
        {
            context.Reply("This command is available only in-game.");
            return;
        }

        if (!Settings.Credits.Transfer.Enabled)
        {
            ReplyCommand(context, "shop.credits.gift.disabled");
            return;
        }

        if (context.Args.Length < 2)
        {
            ReplyCommand(context, "shop.credits.gift.usage");
            return;
        }

        if (!TryResolveTarget(context, context.Args[0], out var target))
        {
            ReplyCommand(context, "shop.credits.target_not_found", context.Args[0]);
            return;
        }

        if (!Settings.Credits.Transfer.AllowSelfTransfer && sender.PlayerID == target.PlayerID)
        {
            ReplyCommand(context, "shop.credits.gift.self_not_allowed");
            return;
        }

        if (!TryParseCreditsAmount(context.Args[1], out var amount))
        {
            ReplyCommand(context, "shop.credits.amount_invalid", context.Args[1]);
            return;
        }

        if (amount < Settings.Credits.Transfer.MinimumAmount)
        {
            ReplyCommand(context, "shop.credits.amount_below_minimum", Settings.Credits.Transfer.MinimumAmount);
            return;
        }

        if (!shopApi.HasCredits(sender, amount))
        {
            ReplyCommand(context, "shop.error.insufficient_credits", "gift", amount);
            return;
        }

        if (!shopApi.SubtractCredits(sender, amount) || !shopApi.AddCredits(target, amount))
        {
            ReplyCommand(context, "shop.error.invalid_amount", "gift");
            return;
        }

        var senderBalance = shopApi.GetCredits(sender);
        ReplyCommand(context, "shop.credits.gift.sender_success", amount, GetPlayerDisplayName(target), senderBalance);

        if (Settings.Credits.Transfer.NotifyReceiver && sender.PlayerID != target.PlayerID)
        {
            SendLocalizedChat(target, "shop.credits.gift.receiver_success", GetPlayerDisplayName(sender), amount);
        }
    }

    private void HandleAdminGiveCreditsCommand(ICommandContext context)
    {
        if (context.Args.Length < 2)
        {
            ReplyCommand(context, "shop.credits.admin.give.usage");
            return;
        }

        if (!TryResolveTarget(context, context.Args[0], out var target))
        {
            ReplyCommand(context, "shop.credits.target_not_found", context.Args[0]);
            return;
        }

        if (!TryParseCreditsAmount(context.Args[1], out var amount))
        {
            ReplyCommand(context, "shop.credits.amount_invalid", context.Args[1]);
            return;
        }

        if (!shopApi.AddCredits(target, amount))
        {
            ReplyCommand(context, "shop.error.invalid_amount", amount);
            return;
        }

        var targetBalance = shopApi.GetCredits(target);
        ReplyCommand(context, "shop.credits.admin.give.success", amount, GetPlayerDisplayName(target), targetBalance);

        if (Settings.Credits.AdminAdjustments.NotifyTargetPlayer)
        {
            var issuerName = context.Sender is IPlayer sender ? GetPlayerDisplayName(sender) : "Console";
            SendLocalizedChat(target, "shop.credits.admin.give.notified_target", issuerName, amount, targetBalance);
        }
    }

    private void HandleAdminRemoveCreditsCommand(ICommandContext context)
    {
        if (context.Args.Length < 2)
        {
            ReplyCommand(context, "shop.credits.admin.remove.usage");
            return;
        }

        if (!TryResolveTarget(context, context.Args[0], out var target))
        {
            ReplyCommand(context, "shop.credits.target_not_found", context.Args[0]);
            return;
        }

        if (!TryParseCreditsAmount(context.Args[1], out var amount))
        {
            ReplyCommand(context, "shop.credits.amount_invalid", context.Args[1]);
            return;
        }

        var currentBalance = shopApi.GetCredits(target);
        if (currentBalance <= 0)
        {
            ReplyCommand(context, "shop.credits.admin.remove.target_has_no_credits", GetPlayerDisplayName(target));
            return;
        }

        var removeAmount = amount;
        if (Settings.Credits.AdminAdjustments.ClampRemovalToAvailableBalance)
        {
            removeAmount = (int)Math.Min(removeAmount, currentBalance);
        }
        else if (amount > currentBalance)
        {
            ReplyCommand(context, "shop.credits.admin.remove.exceeds_balance", GetPlayerDisplayName(target), currentBalance);
            return;
        }

        if (removeAmount <= 0)
        {
            ReplyCommand(context, "shop.credits.admin.remove.target_has_no_credits", GetPlayerDisplayName(target));
            return;
        }

        if (!shopApi.SubtractCredits(target, removeAmount))
        {
            ReplyCommand(context, "shop.error.invalid_amount", removeAmount);
            return;
        }

        var targetBalance = shopApi.GetCredits(target);
        ReplyCommand(context, "shop.credits.admin.remove.success", removeAmount, GetPlayerDisplayName(target), targetBalance);

        if (Settings.Credits.AdminAdjustments.NotifyTargetPlayer)
        {
            var issuerName = context.Sender is IPlayer sender ? GetPlayerDisplayName(sender) : "Console";
            SendLocalizedChat(target, "shop.credits.admin.remove.notified_target", issuerName, removeAmount, targetBalance);
        }
    }

    private void ReplyCommand(ICommandContext context, string key, params object[] args)
    {
        if (context.Sender is IPlayer player && player.IsValid)
        {
            SendLocalizedChat(player, key, args);
            return;
        }

        string message;
        try
        {
            message = args.Length == 0 ? Core.Localizer[key] : Core.Localizer[key, args];
        }
        catch
        {
            message = key;
        }

        context.Reply(message);
    }

    private bool TryParseCreditsAmount(string value, out int amount)
    {
        amount = 0;
        if (!int.TryParse(value, out var parsed))
        {
            return false;
        }

        if (parsed <= 0)
        {
            return false;
        }

        amount = parsed;
        return true;
    }

    private bool TryResolveTarget(ICommandContext context, string input, out IPlayer target)
    {
        target = null!;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        if (context.Sender is IPlayer sender && sender.IsValid)
        {
            var matches = Core.PlayerManager
                .FindTargettedPlayers(sender, input, TargetSearchMode.NoBots | TargetSearchMode.NoMultipleTargets | TargetSearchMode.IncludeSelf)
                .Where(static p => p.IsValid && !p.IsFakeClient)
                .ToList();

            if (matches.Count == 1)
            {
                target = matches[0];
                return true;
            }
        }

        var players = Core.PlayerManager.GetAllValidPlayers()
            .Where(static p => !p.IsFakeClient)
            .ToList();

        if (int.TryParse(input, out var playerId))
        {
            var byPlayerId = players.FirstOrDefault(p => p.PlayerID == playerId);
            if (byPlayerId is not null)
            {
                target = byPlayerId;
                return true;
            }
        }

        if (ulong.TryParse(input, out var steamId))
        {
            var bySteamId = players.FirstOrDefault(p => p.SteamID == steamId);
            if (bySteamId is not null)
            {
                target = bySteamId;
                return true;
            }
        }

        var exactMatches = players
            .Where(p => string.Equals(GetPlayerDisplayName(p), input, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (exactMatches.Count == 1)
        {
            target = exactMatches[0];
            return true;
        }

        var partialMatches = players
            .Where(p => GetPlayerDisplayName(p).Contains(input, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();

        if (partialMatches.Count == 1)
        {
            target = partialMatches[0];
            return true;
        }

        return false;
    }

    private static string GetPlayerDisplayName(IPlayer player)
    {
        try
        {
            var name = player.Controller.PlayerName;
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }
        catch
        {
        }

        return $"#{player.PlayerID}";
    }

    private void HandleAdminReloadCoreCommand(ICommandContext context)
    {
        if (ReloadRuntimeConfiguration(out var error))
        {
            ReplyCommand(context, "shop.admin.reload.success");
            return;
        }

        ReplyCommand(context, "shop.admin.reload.failed", error ?? "unknown error");
    }

    private void HandleAdminStatusCommand(ICommandContext context)
    {
        var hasCookies = playerCookies is not null;
        var hasEconomy = economyApi is not null;
        var wallet = Settings.Credits.WalletName;
        var items = shopApi.GetItems();
        var itemCount = items.Count;
        var categoryCount = items.Select(i => i.Category).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var onlinePlayers = Core.PlayerManager.GetAllValidPlayers().Count(static p => !p.IsFakeClient);

        var timedIncome = Settings.Credits.TimedIncome;
        var shopCorePath = GetPluginPath("ShopCore");
        var templateCount = CountCentralModuleTemplateFiles(shopCorePath);
        var ledgerMode = shopApi.GetLedgerStoreMode();

        ReplyCommand(context, "shop.admin.status.header");
        ReplyCommand(context, "shop.admin.status.dependencies", hasCookies, hasEconomy);
        ReplyCommand(context, "shop.admin.status.wallet", wallet);
        ReplyCommand(context, "shop.admin.status.items", itemCount, categoryCount);
        ReplyCommand(context, "shop.admin.status.players", onlinePlayers);
        ReplyCommand(context, "shop.admin.status.timed_income", timedIncome.Enabled, timedIncome.AmountPerInterval, timedIncome.IntervalSeconds);
        ReplyCommand(context, "shop.admin.status.ledger", ledgerMode);
        ReplyCommand(context, "shop.admin.status.templates", templateCount);
    }

    private void HandleAdminReloadModulesConfigCommand(ICommandContext context)
    {
        if (!ReloadModuleConfigurations(out var copiedTemplates, out var reloadedModules, out var failedModules, out var error))
        {
            ReplyCommand(context, "shop.admin.reload_modules_config.failed", error ?? "unknown error");
            return;
        }

        ReplyCommand(
            context,
            "shop.admin.reload_modules_config.success",
            copiedTemplates,
            reloadedModules,
            failedModules
        );
    }

    private static int CountCentralModuleTemplateFiles(string? shopCorePath)
    {
        if (string.IsNullOrWhiteSpace(shopCorePath))
        {
            return 0;
        }

        var root = Path.Combine(shopCorePath, "resources", "templates", "modules");
        if (!Directory.Exists(root))
        {
            return 0;
        }

        return Directory.GetFiles(root, "*.jsonc", SearchOption.AllDirectories).Length;
    }
}
