using Microsoft.Extensions.Logging;
using ShopCore.Contract;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameEvents;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Plugins;
using SwiftlyS2.Shared.SchemaDefinitions;

namespace ShopCore;

[PluginMetadata(
    Id = "Shop_Rewards",
    Name = "Shop Rewards",
    Author = "T3Marius",
    Version = "1.0.1",
    Description = "ShopCore module with rewards system"
)]
public class Shop_Rewards : BasePlugin
{
    private const string ShopCoreInterfaceKey = "ShopCore.API.v2";
    private const string ModulePluginId = "Shop_Rewards";
    private const string TemplateFileName = "rewards_config.jsonc";
    private const string TemplateSectionName = "Main";
    private RewardsModuleConfig config = new();
    private int? lastRoundWinnerTeam;
    private IShopCoreApiV2? shopApi;

    public Shop_Rewards(ISwiftlyCore core) : base(core)
    {
    }

    public override void UseSharedInterface(IInterfaceManager interfaceManager)
    {
        shopApi = null;

        if (!interfaceManager.HasSharedInterface(ShopCoreInterfaceKey))
        {
            return;
        }

        try
        {
            shopApi = interfaceManager.GetSharedInterface<IShopCoreApiV2>(ShopCoreInterfaceKey);
        }
        catch (Exception ex)
        {
            Core.Logger.LogError(ex, "Failed to resolve shared interface '{InterfaceKey}'.", ShopCoreInterfaceKey);
        }

        TryLoadConfig();
    }

    public override void Load(bool hotReload)
    {
        TryLoadConfig();
    }

    private void TryLoadConfig()
    {
        if (shopApi == null)
        {
            return;
        }

        config = shopApi.LoadModuleConfig<RewardsModuleConfig>(
            ModulePluginId,
            TemplateFileName,
            TemplateSectionName
        );

        lastRoundWinnerTeam = null;
    }

    [GameEventHandler(HookMode.Pre)]
    public HookResult OnMatchEnd(EventCsWinPanelMatch e)
    {
        if (shopApi == null)
        {
            return HookResult.Continue;
        }

        if (IsWarmupPeriod() && config.DisableInWarmup)
        {
            return HookResult.Continue;
        }

        List<IPlayer> onlinePlayers = Core.PlayerManager.GetAllValidPlayers().ToList();
        if (onlinePlayers.Count < config.MinPlayers)
        {
            return HookResult.Continue;
        }

        if (config.MatchWon <= 0 || !TryResolveWinnerTeam(lastRoundWinnerTeam, out var winnerTeam))
        {
            return HookResult.Continue;
        }

        foreach (var player in GetRewardablePlayersOnTeam(winnerTeam))
        {
            shopApi.AddCredits(player, config.MatchWon);
            SendRewardMessage(player, "reward.match_won", config.MatchWon);
        }

        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnRoundStart(EventRoundStart e)
    {
        if (shopApi == null)
        {
            return HookResult.Continue;
        }

        var onlinePlayers = Core.PlayerManager.GetAllValidPlayers().ToList();

        if (IsWarmupPeriod() && config.DisableInWarmup)
        {
            foreach (var player in onlinePlayers)
            {
                NotifyPlayer(player, "module.disabled.warmup");
            }
            return HookResult.Continue;
        }

        if (onlinePlayers.Count < config.MinPlayers)
        {
            foreach (var player in onlinePlayers)
            {
                NotifyPlayer(player, "module.disabled", config.MinPlayers);
            }
            return HookResult.Continue;
        }

        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Pre)]
    public HookResult OnRoundMvp(EventRoundMvp e)
    {
        if (e.UserIdPlayer is not IPlayer player)
        {
            return HookResult.Continue;
        }

        if (shopApi == null)
        {
            return HookResult.Continue;
        }

        if (IsWarmupPeriod() && config.DisableInWarmup)
        {
            return HookResult.Continue;
        }

        List<IPlayer> onlinePlayers = Core.PlayerManager.GetAllValidPlayers().ToList();
        if (onlinePlayers.Count < config.MinPlayers)
        {
            return HookResult.Continue;
        }

        if (config.MVP > 0 && IsRewardablePlayer(player))
        {
            shopApi.AddCredits(player, config.MVP);
            SendRewardMessage(player, "reward.mvp", config.MVP);
        }

        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Pre)]
    public HookResult OnRoundEnd(EventRoundEnd e)
    {
        lastRoundWinnerTeam = e.Winner;

        if (shopApi == null)
        {
            return HookResult.Continue;
        }

        if (IsWarmupPeriod() && config.DisableInWarmup)
        {
            return HookResult.Continue;
        }

        List<IPlayer> onlinePlayers = Core.PlayerManager.GetAllValidPlayers().ToList();
        if (onlinePlayers.Count < config.MinPlayers)
        {
            return HookResult.Continue;
        }

        if (config.RoundWon <= 0 || !TryResolveWinnerTeam(e.Winner, out var winnerTeam))
        {
            return HookResult.Continue;
        }

        foreach (var player in GetRewardablePlayersOnTeam(winnerTeam))
        {
            shopApi.AddCredits(player, config.RoundWon);
            SendRewardMessage(player, "reward.round_won", config.RoundWon);
        }

        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Pre)]
    public HookResult OnPlayerDeath(EventPlayerDeath e)
    {
        if (shopApi == null)
        {
            return HookResult.Continue;
        }

        if (IsWarmupPeriod() && config.DisableInWarmup)
        {
            return HookResult.Continue;
        }

        List<IPlayer> onlinePlayers = Core.PlayerManager.GetAllValidPlayers().ToList();
        if (onlinePlayers.Count < config.MinPlayers)
        {
            return HookResult.Continue;
        }

        var victim = e.UserIdPlayer;

        if (e.AttackerPlayer is IPlayer attacker &&
            IsRewardablePlayer(attacker) &&
            (victim == null || attacker.PlayerID != victim.PlayerID))
        {
            if (config.Kill > 0)
            {
                shopApi.AddCredits(attacker, config.Kill);
                SendRewardMessage(attacker, "reward.kill", config.Kill);
            }

            if (config.Headshot > 0 && e.Headshot)
            {
                shopApi.AddCredits(attacker, config.Headshot);
                SendRewardMessage(attacker, "reward.headshot", config.Headshot);
            }
        }

        if (config.Assist > 0 &&
            e.AssisterPlayer is IPlayer assister &&
            IsRewardablePlayer(assister) &&
            (victim == null || assister.PlayerID != victim.PlayerID))
        {
            shopApi.AddCredits(assister, config.Assist);
            SendRewardMessage(assister, "reward.assist", config.Assist);
        }

        return HookResult.Continue;
    }

    public override void Unload()
    {
        lastRoundWinnerTeam = null;
    }

    private IEnumerable<IPlayer> GetRewardablePlayersOnTeam(Team team)
    {
        return Core.PlayerManager
            .GetAllValidPlayers()
            .Where(p => !p.IsFakeClient && p.Controller.Team == team);
    }

    private static bool IsRewardablePlayer(IPlayer player)
    {
        return player.IsValid && !player.IsFakeClient;
    }

    private static bool TryResolveWinnerTeam(int? rawWinner, out Team winnerTeam)
    {
        winnerTeam = default;
        if (!rawWinner.HasValue)
        {
            return false;
        }

        var value = rawWinner.Value;
        if (value <= 1)
        {
            return false;
        }

        winnerTeam = (Team)value;
        return true;
    }

    private string GetPrefix(IPlayer player)
    {
        var loc = Core.Translation.GetPlayerLocalizer(player);
        if (config.UseCorePrefix)
        {
            var corePrefix = shopApi?.GetShopPrefix(player);
            if (!string.IsNullOrWhiteSpace(corePrefix))
            {
                return corePrefix;
            }
        }

        return loc["shop.prefix"];
    }

    private void NotifyPlayer(IPlayer player, string key, params object[] args)
    {
        var loc = Core.Translation.GetPlayerLocalizer(player);
        var msg = args.Length == 0 ? loc[key] : loc[key, args];
        player.SendChat($"{GetPrefix(player)} {msg}");
    }

    private void SendRewardMessage(IPlayer player, string key, int rewardConfig)
    {
        var loc = Core.Translation.GetPlayerLocalizer(player);
        player.SendChat($"{GetPrefix(player)} {loc[key, rewardConfig]}");
    }
    private bool IsWarmupPeriod()
    {
        return Core.EntitySystem.GetGameRules()?.WarmupPeriod ?? false;
    }
}

internal sealed class RewardsModuleConfig
{
    public int MinPlayers { get; set; } = 4;
    public bool DisableInWarmup { get; set; } = true;
    public bool UseCorePrefix { get; set; } = true;
    public int Kill { get; set; } = 2;
    public int Headshot { get; set; } = 5;
    public int Assist { get; set; } = 1;
    public int RoundWon { get; set; } = 5;
    public int MatchWon { get; set; } = 10;
    public int MVP { get; set; } = 15;
}
