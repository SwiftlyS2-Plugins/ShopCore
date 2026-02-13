using System.Text.Json;
using System.Text.Json.Nodes;
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
    private readonly Dictionary<int, ulong> playerSteamIds = [];
    private CancellationTokenSource? timedIncomeTimer;

    internal ShopCoreConfig Settings { get; private set; } = new();

    private void InitializeConfiguration()
    {
        _ = Core.Configuration
            .InitializeJsonWithModel<ShopCoreConfig>(ConfigFileName, ConfigSectionName);

        EnsureConfigContainsMissingDefaults();

        _ = Core.Configuration
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
        Settings.Ledger ??= new LedgerConfig();
        Settings.Ledger.Persistence ??= new LedgerPersistenceConfig();

        Settings.Commands.OpenShopMenu = NormalizeCommandList(Settings.Commands.OpenShopMenu, ["shop", "store"]);
        Settings.Commands.OpenBuyMenu = NormalizeCommandList(Settings.Commands.OpenBuyMenu, ["buy"]);
        Settings.Commands.OpenInventoryMenu = NormalizeCommandList(Settings.Commands.OpenInventoryMenu, ["inventory", "inv"]);
        Settings.Commands.ShowCredits = NormalizeCommandList(Settings.Commands.ShowCredits, ["credits", "balance"]);
        Settings.Commands.GiftCredits = NormalizeCommandList(Settings.Commands.GiftCredits, ["giftcredits", "gift"]);
        Settings.Commands.Admin.GiveCredits = NormalizeCommandList(Settings.Commands.Admin.GiveCredits, ["givecredits", "addcredits"]);
        Settings.Commands.Admin.RemoveCredits = NormalizeCommandList(Settings.Commands.Admin.RemoveCredits, ["removecredits", "takecredits", "subcredits"]);
        Settings.Commands.Admin.ReloadCore = NormalizeCommandList(Settings.Commands.Admin.ReloadCore, ["shopcorereload", "shopreload"]);
        Settings.Commands.Admin.ReloadModulesConfig = NormalizeCommandList(Settings.Commands.Admin.ReloadModulesConfig, ["reloadmodulesconfig", "shopmodulesreload"]);
        Settings.Commands.Admin.Status = NormalizeCommandList(Settings.Commands.Admin.Status, ["shopcorestatus", "shopstatus"]);

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

        if (Settings.Behavior.PreviewCooldownSeconds < 0f)
        {
            Settings.Behavior.PreviewCooldownSeconds = 0f;
        }

        if (Settings.Ledger.MaxInMemoryEntries < 100)
        {
            Settings.Ledger.MaxInMemoryEntries = 100;
        }

        if (string.IsNullOrWhiteSpace(Settings.Ledger.Persistence.Provider))
        {
            Settings.Ledger.Persistence.Provider = "sqlite";
        }
        else
        {
            Settings.Ledger.Persistence.Provider = Settings.Ledger.Persistence.Provider.Trim().ToLowerInvariant();
        }

        if (string.IsNullOrWhiteSpace(Settings.Ledger.Persistence.ConnectionName))
        {
            Settings.Ledger.Persistence.ConnectionName = "default";
        }
        else
        {
            Settings.Ledger.Persistence.ConnectionName = Settings.Ledger.Persistence.ConnectionName.Trim();
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
        RegisterCommandList(Settings.Commands.Admin.ReloadCore, HandleAdminReloadCoreCommand, Settings.Commands.Admin.Permission);
        RegisterCommandList(Settings.Commands.Admin.ReloadModulesConfig, HandleAdminReloadModulesConfigCommand, Settings.Commands.Admin.Permission);
        RegisterCommandList(Settings.Commands.Admin.Status, HandleAdminStatusCommand, Settings.Commands.Admin.Permission);
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
        Core.Event.OnClientDisconnected += OnClientDisconnected;
    }

    private void UnsubscribeEvents()
    {
        Core.Event.OnClientPutInServer -= OnClientPutInServer;
        Core.Event.OnClientDisconnected -= OnClientDisconnected;
    }

    private void OnClientPutInServer(IOnClientPutInServerEvent @event)
    {
        var player = Core.PlayerManager.GetPlayer(@event.PlayerId);
        if (player is null || !player.IsValid || player.IsFakeClient)
        {
            return;
        }

        playerSteamIds[player.PlayerID] = player.SteamID;
        EnsureStartingBalance(player);
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        var playerId = @event.PlayerId;
        var player = Core.PlayerManager.GetPlayer(playerId);

        ulong steamId = 0;
        if (player is not null)
        {
            steamId = player.SteamID;
        }
        else if (playerSteamIds.TryGetValue(playerId, out var cachedSteamId))
        {
            steamId = cachedSteamId;
        }

        _ = playerSteamIds.Remove(playerId);

        // Persist cookies and economy data when client disconnects to reduce data-loss risk.
        try
        {
            if (player is not null && player.IsValid && !player.IsFakeClient)
            {
                playerCookies.Save(player);
                _ = TrySaveEconomyData(player);
                return;
            }

            if (steamId != 0)
            {
                playerCookies.Save((long)steamId);
                _ = TrySaveEconomyData(steamId);
            }
            else
            {
                _ = TrySaveEconomyData(playerId);
            }
        }
        catch (Exception ex)
        {
            Core.Logger.LogWarning(ex, "Failed to persist player data on disconnect for playerId={PlayerId}.", playerId);
        }
    }

    private bool TrySaveEconomyData(IPlayer player)
    {
        if (TryInvokeEconomySave(typeof(IPlayer), player))
        {
            return true;
        }

        if (player.SteamID != 0 && TrySaveEconomyData(player.SteamID))
        {
            return true;
        }

        return TrySaveEconomyData(player.PlayerID);
    }

    private bool TrySaveEconomyData(ulong steamId)
    {
        if (steamId == 0)
        {
            return false;
        }

        return TryInvokeEconomySave(typeof(ulong), steamId)
            || TryInvokeEconomySave(typeof(long), unchecked((long)steamId));
    }

    private bool TrySaveEconomyData(int playerId)
    {
        if (playerId < 0)
        {
            return false;
        }

        return TryInvokeEconomySave(typeof(int), playerId);
    }

    private bool TryInvokeEconomySave(Type parameterType, object value)
    {
        try
        {
            var method = economyApi.GetType().GetMethod("SaveData", [parameterType]);
            if (method is null)
            {
                return false;
            }

            _ = method.Invoke(economyApi, [value]);
            return true;
        }
        catch (Exception ex)
        {
            Core.Logger.LogWarning(ex, "Failed to invoke Economy SaveData({ParameterType}).", parameterType.Name);
            return false;
        }
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

    private void EnsureConfigContainsMissingDefaults()
    {
        try
        {
            var configPath = Core.Configuration.GetConfigPath(ConfigFileName);
            if (!File.Exists(configPath))
            {
                return;
            }

            var options = new JsonNodeOptions
            {
                PropertyNameCaseInsensitive = true
            };

            var jsonDocOptions = new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            };

            var currentRoot = JsonNode.Parse(File.ReadAllText(configPath), options, jsonDocOptions) as JsonObject;
            if (currentRoot is null)
            {
                return;
            }

            var defaultsRoot = JsonSerializer.SerializeToNode(
                new Dictionary<string, object?>
                {
                    [ConfigSectionName] = new ShopCoreConfig()
                },
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = null,
                    WriteIndented = true
                }) as JsonObject;

            if (defaultsRoot is null)
            {
                return;
            }

            var changed = MergeMissingNodes(currentRoot, defaultsRoot);
            if (!changed)
            {
                return;
            }

            File.WriteAllText(
                configPath,
                currentRoot.ToJsonString(new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = null
                }));

            Core.Logger.LogInformation("ShopCore config schema updated with missing default keys.");
        }
        catch (Exception ex)
        {
            Core.Logger.LogWarning(ex, "Failed to apply automatic ShopCore config schema update.");
        }
    }

    private static bool MergeMissingNodes(JsonObject current, JsonObject defaults)
    {
        var changed = false;

        foreach (var kvp in defaults)
        {
            if (kvp.Value is null)
            {
                continue;
            }

            if (!current.TryGetPropertyValue(kvp.Key, out var currentNode) || currentNode is null)
            {
                current[kvp.Key] = kvp.Value.DeepClone();
                changed = true;
                continue;
            }

            if (currentNode is JsonObject currentObj && kvp.Value is JsonObject defaultObj)
            {
                if (MergeMissingNodes(currentObj, defaultObj))
                {
                    changed = true;
                }
            }
        }

        return changed;
    }

    private bool ReloadRuntimeConfiguration(out string? error)
    {
        error = null;

        try
        {
            StopTimedIncome();
            UnregisterConfiguredCommands();
            InitializeConfiguration();
            shopApi.ConfigureLedgerStore(Settings.Ledger, Core.PluginDataDirectory);
            RegisterConfiguredCommands();
            ApplyStartingBalanceToConnectedPlayers();
            StartTimedIncome();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Core.Logger.LogError(ex, "Failed to reload ShopCore runtime configuration.");
            return false;
        }
    }

    private int CountCentralModuleConfigs()
    {
        try
        {
            var centralModulesRoot = GetCentralModuleConfigsDirectoryPath();
            Directory.CreateDirectory(centralModulesRoot);
            return Directory.GetFiles(centralModulesRoot, "*.*", SearchOption.TopDirectoryOnly)
                .Count(static file =>
                    file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".jsonc", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            Core.Logger.LogWarning(ex, "Failed while preparing configs/plugins/ShopCore/modules.");
            return 0;
        }
    }

    private bool ReloadModuleConfigurations(out int configFileCount, out int reloadedModules, out int failedModules, out string? error)
    {
        configFileCount = 0;
        reloadedModules = 0;
        failedModules = 0;
        error = null;

        try
        {
            configFileCount = CountCentralModuleConfigs();

            var loadedPlugins = new HashSet<string>(Core.PluginManager.GetAllPlugins(), StringComparer.OrdinalIgnoreCase);
            var knownModules = shopApi.GetKnownModulePluginIds();
            var moduleIds = knownModules
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .Select(static id => id!)
                .Where(id => !string.Equals(id, "ShopCore", StringComparison.OrdinalIgnoreCase))
                .Where(loadedPlugins.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var moduleId in moduleIds)
            {
                if (Core.PluginManager.ReloadPlugin(moduleId, silent: false))
                {
                    reloadedModules++;
                    continue;
                }

                failedModules++;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Core.Logger.LogError(ex, "Failed to reload module configurations.");
            return false;
        }
    }
}

