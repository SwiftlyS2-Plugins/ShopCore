using Cookies.Contract;
using Economy.Contract;
using Microsoft.Extensions.Logging;
using ShopCore.Contract;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Database;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Plugins;
using SwiftlyS2.Shared.Translation;

namespace ShopCore;

[PluginMetadata(
    Id = "ShopCore",
    Version = "v1.0.0",
    Name = "ShopCore",
    Author = "T3Marius",
    Description = "Core shop plugin exposing items and credits API."
)]
public partial class ShopCore : BasePlugin
{
    public const string ShopCoreInterfaceKey = "ShopCore.API.v1";
    public const string PlayerCookiesInterfaceKey = "Cookies.Player.v1";
    public const string PlayerCookiesInterfaceKeyLegacy = "Cookies.Player.V1";
    public const string EconomyInterfaceKey = "Economy.API.v1";
    public const string EconomyInterfaceKeyLegacy = "Economy.API.V1";

    private readonly ShopCoreApiV1 shopApi;

    public ShopCore(ISwiftlyCore core) : base(core)
    {
        shopApi = new ShopCoreApiV1(this);
    }

    public IPlayerCookiesAPIv1 playerCookies = null!;
    public IEconomyAPIv1 economyApi = null!;

    public override void ConfigureSharedInterface(IInterfaceManager interfaceManager)
    {
        interfaceManager.AddSharedInterface<IShopCoreApiV1, ShopCoreApiV1>(ShopCoreInterfaceKey, shopApi);
    }

    public override void UseSharedInterface(IInterfaceManager interfaceManager)
    {
        playerCookies = null!;
        economyApi = null!;

        playerCookies = ResolveSharedInterface<IPlayerCookiesAPIv1>(
            interfaceManager,
            [PlayerCookiesInterfaceKey, PlayerCookiesInterfaceKeyLegacy]
        )!;

        economyApi = ResolveSharedInterface<IEconomyAPIv1>(
            interfaceManager,
            [EconomyInterfaceKey, EconomyInterfaceKeyLegacy]
        )!;
    }

    public override void OnSharedInterfaceInjected(IInterfaceManager interfaceManager)
    {
        StopTimedIncome();
        UnsubscribeEvents();
        UnregisterConfiguredCommands();

        if (playerCookies is null || economyApi is null)
        {
            var hasCookies = interfaceManager.HasSharedInterface(PlayerCookiesInterfaceKey)
                || interfaceManager.HasSharedInterface(PlayerCookiesInterfaceKeyLegacy);
            var hasEconomy = interfaceManager.HasSharedInterface(EconomyInterfaceKey)
                || interfaceManager.HasSharedInterface(EconomyInterfaceKeyLegacy);

            Core.Logger.LogError(
                "ShopCore dependencies are missing or incompatible. Required interfaces: '{CookiesKey}', '{EconomyKey}'. " +
                "HasSharedInterface(Cookies)={HasCookies}, HasSharedInterface(Economy)={HasEconomy}, " +
                "Expected Cookies contract assembly='{CookiesAssembly}', Expected Economy contract assembly='{EconomyAssembly}'.",
                $"{PlayerCookiesInterfaceKey} | {PlayerCookiesInterfaceKeyLegacy}",
                $"{EconomyInterfaceKey} | {EconomyInterfaceKeyLegacy}",
                hasCookies,
                hasEconomy,
                typeof(IPlayerCookiesAPIv1).Assembly.FullName,
                typeof(IEconomyAPIv1).Assembly.FullName
            );
            return;
        }

        economyApi.EnsureWalletKind(shopApi.WalletKind);
        shopApi.ConfigureLedgerStore(Settings.Ledger, Core.PluginDataDirectory);
        RegisterConfiguredCommands();
        SubscribeEvents();
        ApplyStartingBalanceToConnectedPlayers();
        StartTimedIncome();
    }

    public override void Load(bool hotReload)
    {
        InitializeConfiguration();
    }

    private T? ResolveSharedInterface<T>(IInterfaceManager interfaceManager, IEnumerable<string> keys) where T : class
    {
        foreach (var key in keys.Distinct(StringComparer.Ordinal))
        {
            if (!interfaceManager.HasSharedInterface(key))
            {
                continue;
            }

            try
            {
                var resolved = interfaceManager.GetSharedInterface<T>(key);
                Core.Logger.LogInformation("Resolved shared interface '{InterfaceType}' using key '{InterfaceKey}'.", typeof(T).FullName, key);
                return resolved;
            }
            catch (Exception ex)
            {
                Core.Logger.LogError(ex, "Failed to resolve shared interface '{InterfaceKey}'.", key);
            }
        }

        return null;
    }

    public override void Unload()
    {
        StopTimedIncome();
        UnsubscribeEvents();
        UnregisterConfiguredCommands();
        shopApi.DisposeLedgerStore();
    }

    internal string? GetPluginPath(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            return null;
        }

        try
        {
            return Core.PluginManager.GetPluginPath(pluginId);
        }
        catch
        {
            return null;
        }
    }

    internal string GetCentralModuleConfigsDirectoryPath()
    {
        return Path.Combine(
            Core.CSGODirectory,
            "addons",
            "swiftlys2",
            "configs",
            "plugins",
            "ShopCore",
            "modules"
        );
    }

    internal string BuildCentralModuleConfigPath(string modulePluginId, string normalizedRelativeConfigPath)
    {
        var normalized = string.IsNullOrWhiteSpace(normalizedRelativeConfigPath)
            ? "items_config.jsonc"
            : normalizedRelativeConfigPath.Trim();

        var fileName = Path.GetFileName(normalized);
        var safeFileName = SanitizeFileNameSegment(fileName);

        return Path.Combine(GetCentralModuleConfigsDirectoryPath(), safeFileName);
    }

    internal string BuildLegacyModuleScopedCentralModuleConfigPath(string modulePluginId, string normalizedRelativeConfigPath)
    {
        var moduleId = string.IsNullOrWhiteSpace(modulePluginId) ? "UnknownModule" : modulePluginId.Trim();
        var normalized = string.IsNullOrWhiteSpace(normalizedRelativeConfigPath)
            ? "items_config.jsonc"
            : normalizedRelativeConfigPath.Trim();

        var safeModuleId = SanitizeFileNameSegment(moduleId);
        var safePath = normalized
            .Replace('/', '_')
            .Replace('\\', '_');
        safePath = SanitizeFileNameSegment(safePath);

        return Path.Combine(GetCentralModuleConfigsDirectoryPath(), $"{safeModuleId}__{safePath}");
    }

    private static string SanitizeFileNameSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "config";
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray());
        while (sanitized.Contains("..", StringComparison.Ordinal))
        {
            sanitized = sanitized.Replace("..", "__", StringComparison.Ordinal);
        }

        return string.IsNullOrWhiteSpace(sanitized) ? "config" : sanitized;
    }

    internal void LogWarning(string message, params object[] args)
    {
        Core.Logger.LogWarning(message, args);
    }

    internal void LogWarning(Exception exception, string message, params object[] args)
    {
        Core.Logger.LogWarning(exception, message, args);
    }

    internal void LogDebug(string message, params object[] args)
    {
        Core.Logger.LogDebug(message, args);
    }

    internal DatabaseConnectionInfo? TryGetDatabaseConnectionInfo(string connectionName)
    {
        try
        {
            return Core.Database.GetConnectionInfo(connectionName);
        }
        catch (Exception ex)
        {
            Core.Logger.LogWarning(ex, "Failed to resolve Core.Database connection info for '{ConnectionName}'.", connectionName);
            return null;
        }
    }

    internal void SendLocalizedChat(IPlayer player, string key, params object[] args)
    {
        Core.Scheduler.NextWorldUpdate(() =>
        {
            try
            {
                if (player is null || !player.IsValid)
                {
                    return;
                }

                var message = Localize(player, key, args);
                var prefix = TryGetChatPrefix(player);

                if (!string.IsNullOrWhiteSpace(prefix) && !message.StartsWith(prefix, StringComparison.Ordinal))
                {
                    player.SendChat($"{prefix} {message}");
                    return;
                }

                player.SendChat(message);
            }
            catch (Exception ex)
            {
                Core.Logger.LogWarning(ex, "Failed to send localized chat message for key '{TranslationKey}'.", key);
            }
        });
    }

    internal void SendChatRaw(IPlayer player, string message)
    {
        Core.Scheduler.NextWorldUpdate(() =>
        {
            try
            {
                if (player is null || !player.IsValid)
                {
                    return;
                }

                var prefix = TryGetChatPrefix(player);
                if (!string.IsNullOrWhiteSpace(prefix) && !message.StartsWith(prefix, StringComparison.Ordinal))
                {
                    player.SendChat($"{prefix} {message}");
                    return;
                }

                player.SendChat(message);
            }
            catch (Exception ex)
            {
                Core.Logger.LogWarning(ex, "Failed to send raw chat message.");
            }
        });
    }

    internal string Localize(IPlayer player, string key, params object[] args)
    {
        try
        {
            var localizer = Core.Translation.GetPlayerLocalizer(player);
            return args.Length == 0 ? localizer[key] : localizer[key, args];
        }
        catch
        {
            if (args.Length == 0)
            {
                return key;
            }

            try
            {
                return string.Format(key, args);
            }
            catch
            {
                return key;
            }
        }
    }

    private string TryGetChatPrefix(IPlayer player)
    {
        try
        {
            var localizer = Core.Translation.GetPlayerLocalizer(player);
            var prefix = localizer["shop.prefix"];

            if (string.Equals(prefix, "shop.prefix", StringComparison.Ordinal))
            {
                return string.Empty;
            }

            return prefix;
        }
        catch
        {
            return string.Empty;
        }
    }
}
