using Cookies.Contract;
using Economy.Contract;
using Microsoft.Extensions.Logging;
using ShopCore.Contract;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Plugins;
using SwiftlyS2.Shared.Translation;

namespace ShopCore;

[PluginMetadata(
    Id = "ShopCore",
    Version = "1.0.0",
    Name = "ShopCore",
    Author = "mariu",
    Description = "Core shop plugin exposing items and credits API."
)]
public partial class ShopCore : BasePlugin
{
    public const string ShopCoreInterfaceKey = "ShopCore.API.v1";
    public const string PlayerCookiesInterfaceKey = "Cookies.Player.V1";
    public const string EconomyInterfaceKey = "Economy.API.v1";

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

        if (interfaceManager.HasSharedInterface(PlayerCookiesInterfaceKey))
        {
            try
            {
                playerCookies = interfaceManager.GetSharedInterface<IPlayerCookiesAPIv1>(PlayerCookiesInterfaceKey);
            }
            catch (Exception ex)
            {
                Core.Logger.LogError(ex, "Failed to resolve shared interface '{InterfaceKey}'.", PlayerCookiesInterfaceKey);
            }
        }

        if (interfaceManager.HasSharedInterface(EconomyInterfaceKey))
        {
            try
            {
                economyApi = interfaceManager.GetSharedInterface<IEconomyAPIv1>(EconomyInterfaceKey);
            }
            catch (Exception ex)
            {
                Core.Logger.LogError(ex, "Failed to resolve shared interface '{InterfaceKey}'.", EconomyInterfaceKey);
            }
        }
    }

    public override void OnSharedInterfaceInjected(IInterfaceManager interfaceManager)
    {
        StopTimedIncome();
        UnsubscribeEvents();
        UnregisterConfiguredCommands();

        if (playerCookies is null || economyApi is null)
        {
            Core.Logger.LogError(
                "ShopCore dependencies are missing or incompatible. Required interfaces: '{CookiesKey}', '{EconomyKey}'.",
                PlayerCookiesInterfaceKey,
                EconomyInterfaceKey
            );
            return;
        }

        economyApi.EnsureWalletKind(shopApi.WalletKind);
        RegisterConfiguredCommands();
        SubscribeEvents();
        ApplyStartingBalanceToConnectedPlayers();
        StartTimedIncome();
    }

    public override void Load(bool hotReload)
    {
        InitializeConfiguration();
    }

    public override void Unload()
    {
        StopTimedIncome();
        UnsubscribeEvents();
        UnregisterConfiguredCommands();
    }

    internal void SendLocalizedChat(IPlayer player, string key, params object[] args)
    {
        var message = Localize(player, key, args);
        player.SendChat(message);
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
}
