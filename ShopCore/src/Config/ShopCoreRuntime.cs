using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.Players;

namespace ShopCore;

public partial class ShopCore
{
    private const string ConfigFileName = "shopcore.jsonc";
    private const string ConfigSectionName = "Main";
    private const string StartingBalanceCookieKey = "shopcore:credits:starting-balance-applied";

    private readonly List<Guid> registeredCommands = [];
    private CancellationTokenSource? timedIncomeTimer;

    internal ShopCoreConfig Settings { get; private set; } = new();

    private void InitializeConfiguration()
    {
        _ = Core.Configuration
            .InitializeJsonWithModel<ShopCoreConfig>(ConfigFileName, ConfigSectionName)
            .Configure(builder => { _ = builder.AddJsonFile(ConfigFileName, optional: false, reloadOnChange: true); });

        Settings = Core.Configuration.Manager
            .GetSection(ConfigSectionName)
            .Get<ShopCoreConfig>() ?? new ShopCoreConfig();

        NormalizeConfiguration();
    }

    private void NormalizeConfiguration()
    {
        Settings.Commands ??= new CommandsConfig();
        Settings.Commands.Admin ??= new AdminCommandsConfig();
        Settings.Credits ??= new CreditsConfig();
        Settings.Credits.TimedIncome ??= new TimedIncomeConfig();
        Settings.Credits.Transfer ??= new CreditTransferConfig();
        Settings.Credits.AdminAdjustments ??= new AdminCreditAdjustmentsConfig();
        Settings.Menus ??= new MenusConfig();
        Settings.Behavior ??= new BehaviorConfig();

        Settings.Commands.OpenShopMenu = NormalizeCommandList(Settings.Commands.OpenShopMenu, ["shop", "store"]);
        Settings.Commands.OpenBuyMenu = NormalizeCommandList(Settings.Commands.OpenBuyMenu, ["buy"]);
        Settings.Commands.OpenInventoryMenu = NormalizeCommandList(Settings.Commands.OpenInventoryMenu, ["inventory", "inv"]);
        Settings.Commands.ShowCredits = NormalizeCommandList(Settings.Commands.ShowCredits, ["credits", "balance"]);
        Settings.Commands.GiftCredits = NormalizeCommandList(Settings.Commands.GiftCredits, ["giftcredits", "gift"]);
        Settings.Commands.Admin.GiveCredits = NormalizeCommandList(Settings.Commands.Admin.GiveCredits, ["givecredits", "addcredits"]);
        Settings.Commands.Admin.RemoveCredits = NormalizeCommandList(Settings.Commands.Admin.RemoveCredits, ["removecredits", "takecredits", "subcredits"]);

        if (string.IsNullOrWhiteSpace(Settings.Commands.Admin.Permission))
        {
            Settings.Commands.Admin.Permission = "shopcore.admin.credits";
        }
        else
        {
            Settings.Commands.Admin.Permission = Settings.Commands.Admin.Permission.Trim();
        }

        if (string.IsNullOrWhiteSpace(Settings.Credits.WalletName))
        {
            Settings.Credits.WalletName = ShopCoreApiV1.DefaultWalletKind;
        }
        else
        {
            Settings.Credits.WalletName = Settings.Credits.WalletName.Trim();
        }

        if (Settings.Credits.StartingBalance < 0)
        {
            Settings.Credits.StartingBalance = 0;
        }

        if (Settings.Credits.TimedIncome.AmountPerInterval < 0)
        {
            Settings.Credits.TimedIncome.AmountPerInterval = 0;
        }

        if (Settings.Credits.TimedIncome.IntervalSeconds < 1f)
        {
            Settings.Credits.TimedIncome.IntervalSeconds = 1f;
        }

        if (Settings.Credits.Transfer.MinimumAmount < 1)
        {
            Settings.Credits.Transfer.MinimumAmount = 1;
        }

        if (Settings.Menus.MaxVisibleItems < 1 || Settings.Menus.MaxVisibleItems > 5)
        {
            Settings.Menus.MaxVisibleItems = 5;
        }

        if (string.IsNullOrWhiteSpace(Settings.Menus.DefaultCommentTranslationKey))
        {
            Settings.Menus.DefaultCommentTranslationKey = "shop.menu.comment";
        }

        if (Settings.Behavior.DefaultSellRefundRatio < 0m)
        {
            Settings.Behavior.DefaultSellRefundRatio = 0m;
        }

        if (Settings.Behavior.DefaultSellRefundRatio > 1m)
        {
            Settings.Behavior.DefaultSellRefundRatio = 1m;
        }
    }

    private static List<string> NormalizeCommandList(List<string>? values, IEnumerable<string> fallback)
    {
        var normalized = (values ?? [])
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalized.Count > 0)
        {
            return normalized;
        }

        return fallback.ToList();
    }

    private void RegisterConfiguredCommands()
    {
        RegisterCommandList(Settings.Commands.OpenShopMenu, HandleOpenShopMenuCommand);
        RegisterCommandList(Settings.Commands.OpenBuyMenu, HandleOpenBuyMenuCommand);
        RegisterCommandList(Settings.Commands.OpenInventoryMenu, HandleOpenInventoryMenuCommand);
        RegisterCommandList(Settings.Commands.ShowCredits, HandleShowCreditsCommand);
        RegisterCommandList(Settings.Commands.GiftCredits, HandleGiftCreditsCommand);

        RegisterCommandList(Settings.Commands.Admin.GiveCredits, HandleAdminGiveCreditsCommand, Settings.Commands.Admin.Permission);
        RegisterCommandList(Settings.Commands.Admin.RemoveCredits, HandleAdminRemoveCreditsCommand, Settings.Commands.Admin.Permission);
    }

    private void RegisterCommandList(IEnumerable<string> commands, ICommandService.CommandListener handler, string permission = "")
    {
        foreach (var command in commands)
        {
            if (Core.Command.IsCommandRegistered(command))
            {
                Core.Logger.LogWarning("Cannot register command '{Command}' because it is already registered.", command);
                continue;
            }

            var guid = Core.Command.RegisterCommand(command, handler, Settings.Commands.RegisterAsRawCommands, permission);
            registeredCommands.Add(guid);
        }
    }

    private void UnregisterConfiguredCommands()
    {
        foreach (var guid in registeredCommands)
        {
            Core.Command.UnregisterCommand(guid);
        }

        registeredCommands.Clear();
    }

    private void SubscribeEvents()
    {
        Core.Event.OnClientPutInServer += OnClientPutInServer;
    }

    private void UnsubscribeEvents()
    {
        Core.Event.OnClientPutInServer -= OnClientPutInServer;
    }

    private void OnClientPutInServer(IOnClientPutInServerEvent @event)
    {
        var player = Core.PlayerManager.GetPlayer(@event.PlayerId);
        if (player is null || !player.IsValid || player.IsFakeClient)
        {
            return;
        }

        EnsureStartingBalance(player);
    }

    private void ApplyStartingBalanceToConnectedPlayers()
    {
        foreach (var player in Core.PlayerManager.GetAllValidPlayers())
        {
            if (player.IsFakeClient)
            {
                continue;
            }

            EnsureStartingBalance(player);
        }
    }

    private void EnsureStartingBalance(IPlayer player)
    {
        var target = Settings.Credits.StartingBalance;
        if (target <= 0)
        {
            return;
        }

        if (Settings.Credits.GrantStartingBalanceOncePerPlayer &&
            playerCookies.GetOrDefault(player, StartingBalanceCookieKey, false))
        {
            return;
        }

        var current = economyApi.GetPlayerBalance(player, shopApi.WalletKind);
        if (current < target)
        {
            economyApi.SetPlayerBalance(player, shopApi.WalletKind, target);

            if (Settings.Credits.NotifyWhenStartingBalanceApplied)
            {
                SendLocalizedChat(player, "shop.credits.starting_balance_applied", target);
            }
        }

        if (Settings.Credits.GrantStartingBalanceOncePerPlayer)
        {
            playerCookies.Set(player, StartingBalanceCookieKey, true);
            playerCookies.Save(player);
        }
    }

    private void StartTimedIncome()
    {
        var timedIncome = Settings.Credits.TimedIncome;
        if (!timedIncome.Enabled || timedIncome.AmountPerInterval <= 0)
        {
            return;
        }

        timedIncomeTimer = Core.Scheduler.DelayAndRepeatBySeconds(
            timedIncome.IntervalSeconds,
            timedIncome.IntervalSeconds,
            GiveTimedIncome
        );
    }

    private void StopTimedIncome()
    {
        if (timedIncomeTimer is null)
        {
            return;
        }

        timedIncomeTimer.Cancel();
        timedIncomeTimer.Dispose();
        timedIncomeTimer = null;
    }

    private void GiveTimedIncome()
    {
        var timedIncome = Settings.Credits.TimedIncome;
        if (!timedIncome.Enabled || timedIncome.AmountPerInterval <= 0)
        {
            return;
        }

        foreach (var player in Core.PlayerManager.GetAllValidPlayers())
        {
            if (player.IsFakeClient)
            {
                continue;
            }

            EnsureStartingBalance(player);
            economyApi.AddPlayerBalance(player, shopApi.WalletKind, timedIncome.AmountPerInterval);

            if (timedIncome.NotifyPlayers)
            {
                SendLocalizedChat(player, "shop.credits.timed_income", timedIncome.AmountPerInterval, timedIncome.IntervalSeconds);
            }
        }
    }
}
