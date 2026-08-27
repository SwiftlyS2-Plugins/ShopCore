using Microsoft.Extensions.Logging;
using ShopCore.Contract;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Plugins;

namespace ShopCore;

[PluginMetadata(
    Id = "Shop_Coinflip",
    Name = "Shop Coinflip",
    Author = "T3Marius",
    Version = "1.0.0",
    Description = "ShopCore coinflip betting module"
)]
public class Shop_Coinflip : BasePlugin
{
    private const string ShopCoreInterfaceKey = "ShopCore.API.v2";
    private const string ModulePluginId = "Shop_Coinflip";
    private const string TemplateFileName = "coinflip_config.jsonc";
    private const string TemplateSectionName = "Main";
    private const string FallbackShopPrefix = "[gold]★[red] [Store][default]";

    private readonly Dictionary<ulong, DateTimeOffset> cooldownBySteam = new();
    private readonly List<Guid> registeredCommands = new();
    private IShopCoreApiV2? shopApi;
    private CoinflipModuleConfig settings = new();

    public Shop_Coinflip(ISwiftlyCore core) : base(core) { }

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
            Core.Logger.LogWarning(ex, "Failed to resolve shared interface '{InterfaceKey}'.", ShopCoreInterfaceKey);
        }
    }

    public override void OnSharedInterfaceInjected(IInterfaceManager interfaceManager)
    {
        if (shopApi is null)
        {
            Core.Logger.LogWarning("ShopCore API is not available. Coinflip commands will not be registered.");
            return;
        }

        LoadConfigAndRegisterCommands();
    }

    public override void Load(bool hotReload)
    {
        if (shopApi is not null)
        {
            LoadConfigAndRegisterCommands();
        }
    }

    public override void Unload()
    {
        UnregisterCommands();
        cooldownBySteam.Clear();
    }

    private void LoadConfigAndRegisterCommands()
    {
        if (shopApi is null)
        {
            return;
        }

        settings = shopApi.LoadModuleConfig<CoinflipModuleConfig>(
            ModulePluginId,
            TemplateFileName,
            TemplateSectionName
        );
        NormalizeConfig(settings);

        if (!settings.Commands.Any())
        {
            settings = CreateDefaultConfig();
            _ = shopApi.SaveModuleConfig(
                ModulePluginId,
                settings,
                TemplateFileName,
                TemplateSectionName,
                overwrite: true
            );
        }

        UnregisterCommands();
        RegisterCommands();

        Core.Logger.LogInformation(
            "Shop_Coinflip initialized. Commands={CommandsCount}, MinBet={MinBet}, MaxBet={MaxBet}, Cooldown={Cooldown}s",
            settings.Commands.Count,
            settings.MinimumBet,
            settings.MaximumBet,
            settings.BetCooldownSeconds
        );
    }

    private void RegisterCommands()
    {
        foreach (var command in settings.Commands)
        {
            if (Core.Command.IsCommandRegistered(command))
            {
                Core.Logger.LogWarning("Cannot register coinflip command '{Command}' because it is already registered.", command);
                continue;
            }

            var guid = Core.Command.RegisterCommand(
                command,
                HandleCoinflipCommand,
                settings.RegisterAsRawCommands,
                settings.CommandPermission
            );
            registeredCommands.Add(guid);
        }
    }

    private void UnregisterCommands()
    {
        foreach (var guid in registeredCommands)
        {
            Core.Command.UnregisterCommand(guid);
        }

        registeredCommands.Clear();
    }

    private void HandleCoinflipCommand(ICommandContext context)
    {
        if (shopApi is null)
        {
            context.Reply("ShopCore API is unavailable.");
            return;
        }

        if (!settings.Enabled)
        {
            Reply(context, "coinflip.disabled");
            return;
        }

        if (context.Sender is not IPlayer player || !player.IsValid || player.IsFakeClient)
        {
            context.Reply("This command is available only in-game.");
            return;
        }

        if (context.Args.Length < 1)
        {
            Reply(context, "coinflip.usage", settings.Commands.FirstOrDefault() ?? "coinflip");
            return;
        }

        if (!int.TryParse(context.Args[0], out var bet))
        {
            Reply(context, "coinflip.invalid_bet", settings.MinimumBet, settings.MaximumBet);
            return;
        }

        if (bet < settings.MinimumBet)
        {
            Reply(context, "coinflip.min_bet", settings.MinimumBet);
            return;
        }

        if (bet > settings.MaximumBet)
        {
            Reply(context, "coinflip.max_bet", settings.MaximumBet);
            return;
        }

        if (settings.BetCooldownSeconds > 0 &&
            cooldownBySteam.TryGetValue(player.SteamID, out var nextAllowedTime))
        {
            var now = DateTimeOffset.UtcNow;
            if (now < nextAllowedTime)
            {
                var remaining = (int)Math.Ceiling((nextAllowedTime - now).TotalSeconds);
                Reply(context, "coinflip.cooldown", remaining);
                return;
            }
        }

        if (!shopApi.HasCredits(player, bet))
        {
            Reply(context, "coinflip.no_credits", bet);
            return;
        }

        var balanceBeforeBet = shopApi.GetCredits(player);
        if (!shopApi.SubtractCredits(player, bet))
        {
            Reply(context, "coinflip.internal_error");
            return;
        }

        if (settings.BetCooldownSeconds > 0)
        {
            cooldownBySteam[player.SteamID] = DateTimeOffset.UtcNow.AddSeconds(settings.BetCooldownSeconds);
        }

        var balanceAfterBet = balanceBeforeBet - bet;
        var won = Random.Shared.NextDouble() <= Math.Clamp(settings.WinChance, 0.0, 1.0);
        if (won)
        {
            var reward = Math.Max(1, (int)Math.Round(bet * settings.WinMultiplier, MidpointRounding.AwayFromZero));
            if (!shopApi.AddCredits(player, reward))
            {
                Reply(context, "coinflip.internal_error");
                return;
            }

            var wonBalance = balanceAfterBet + reward;
            Reply(context, "coinflip.won", reward, wonBalance);
            return;
        }

        Reply(context, "coinflip.lost", bet, balanceAfterBet);
    }

    private void Reply(ICommandContext context, string key, params object[] args)
    {
        var player = context.Sender as IPlayer;
        var message = BuildPrefixedMessage(player, key, args);

        if (player is not null && player.IsValid)
        {
            player.SendChat(message);
            return;
        }

        context.Reply(message);
    }

    private string BuildPrefixedMessage(IPlayer? player, string key, params object[] args)
    {
        string body;
        string prefix;

        if (player is not null && player.IsValid)
        {
            var loc = Core.Translation.GetPlayerLocalizer(player);
            body = args.Length == 0 ? loc[key] : loc[key, args];
            prefix = ResolvePrefix(player);
            return $"{prefix} {body}";
        }

        body = args.Length == 0 ? Core.Localizer[key] : Core.Localizer[key, args];
        prefix = Core.Localizer["shop.prefix"];
        if (string.IsNullOrWhiteSpace(prefix) || string.Equals(prefix, "shop.prefix", StringComparison.Ordinal))
        {
            prefix = FallbackShopPrefix;
        }

        return $"{prefix} {body}";
    }

    private string ResolvePrefix(IPlayer player)
    {
        var loc = Core.Translation.GetPlayerLocalizer(player);
        if (settings.UseCorePrefix)
        {
            var corePrefix = shopApi?.GetShopPrefix(player);
            if (!string.IsNullOrWhiteSpace(corePrefix))
            {
                return corePrefix;
            }
        }

        var modulePrefix = loc["shop.prefix"];
        if (string.IsNullOrWhiteSpace(modulePrefix) || string.Equals(modulePrefix, "shop.prefix", StringComparison.Ordinal))
        {
            return FallbackShopPrefix;
        }

        return modulePrefix;
    }

    private static void NormalizeConfig(CoinflipModuleConfig config)
    {
        config.Commands = NormalizeCommandList(config.Commands, ["coinflip"]);

        if (config.MinimumBet < 1)
        {
            config.MinimumBet = 1;
        }

        if (config.MaximumBet < config.MinimumBet)
        {
            config.MaximumBet = config.MinimumBet;
        }

        if (config.BetCooldownSeconds < 0)
        {
            config.BetCooldownSeconds = 0;
        }

        if (config.WinMultiplier <= 0f)
        {
            config.WinMultiplier = 2f;
        }

        config.WinChance = Math.Clamp(config.WinChance, 0.01, 1.0);
        config.CommandPermission ??= string.Empty;
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

    private static CoinflipModuleConfig CreateDefaultConfig()
    {
        return new CoinflipModuleConfig
        {
            Enabled = true,
            Commands = ["coinflip", "cf"],
            RegisterAsRawCommands = false,
            CommandPermission = string.Empty,
            MinimumBet = 10,
            MaximumBet = 5000,
            BetCooldownSeconds = 15,
            WinChance = 0.5,
            WinMultiplier = 2.0f
        };
    }
}

internal sealed class CoinflipModuleConfig
{
    public bool UseCorePrefix { get; set; } = true;
    public bool Enabled { get; set; } = true;
    public List<string> Commands { get; set; } = [];
    public bool RegisterAsRawCommands { get; set; } = false;
    public string CommandPermission { get; set; } = string.Empty;
    public int MinimumBet { get; set; } = 10;
    public int MaximumBet { get; set; } = 5000;
    public int BetCooldownSeconds { get; set; } = 15;
    public double WinChance { get; set; } = 0.5;
    public float WinMultiplier { get; set; } = 2.0f;
}
