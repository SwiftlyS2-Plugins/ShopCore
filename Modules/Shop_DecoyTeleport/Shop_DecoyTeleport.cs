using Microsoft.Extensions.Logging;
using ShopCore.Contract;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameEvents;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Plugins;
using SwiftlyS2.Shared.SchemaDefinitions;

namespace ShopCore;

[PluginMetadata(
    Id = "Shop_DecoyTeleport",
    Name = "Shop DecoyTeleport",
    Author = "T3Marius",
    Version = "1.0.0",
    Description = "ShopCore module with decoy teleport items."
)]
public class Shop_DecoyTeleport : BasePlugin
{
    private const string ShopCoreInterfaceKey = "ShopCore.API.v1";
    private const string ModulePluginId = "Shop_DecoyTeleport";
    private const string TemplateFileName = "decoy_teleport_config.jsonc";
    private const string TemplateSectionName = "Main";
    private const string DefaultCategory = "Abilities";
    private const float DefaultTeleportZOffset = 8f;

    private IShopCoreApiV1? shopApi;
    private readonly object stateSync = new();
    private readonly HashSet<int> armedPlayers = [];
    private readonly Dictionary<ulong, long> cooldownUntilUnixSeconds = [];
    private DecoyModuleConfig Config { get; set; } = new();
    private bool handlersRegistered;
    private bool itemRegistered;

    public Shop_DecoyTeleport(ISwiftlyCore core) : base(core)
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
            shopApi = interfaceManager.GetSharedInterface<IShopCoreApiV1>(ShopCoreInterfaceKey);
        }
        catch (Exception ex)
        {
            Core.Logger.LogError(ex, "Failed to resolve shared interface '{InterfaceKey}'.", ShopCoreInterfaceKey);
        }
    }

    public override void OnSharedInterfaceInjected(IInterfaceManager interfaceManager)
    {
        if (shopApi is null)
        {
            Core.Logger.LogWarning("ShopCore API is not available. Decoy Teleport items will not be registered.");
            return;
        }

        InitializeModule();
    }

    public override void Load(bool hotReload)
    {
        InitializeModule();
    }

    private void InitializeModule()
    {
        if (shopApi is null)
        {
            return;
        }

        if (handlersRegistered)
        {
            return;
        }

        Config = shopApi.LoadModuleConfig<DecoyModuleConfig>(
            ModulePluginId,
            TemplateFileName,
            TemplateSectionName
        );
        NormalizeConfig(Config);

        if (!TryParseTeam(Config.Decoy.Team, out var team))
        {
            team = ShopItemTeam.Any;
        }

        if (!TryParseItemType(Config.Decoy.Type, out var type))
        {
            type = ShopItemType.Consumable;
        }

        itemRegistered = shopApi.RegisterItem(new ShopItemDefinition(
            Id: Config.Decoy.Id,
            DisplayName: Core.Localizer["decoy_teleport_module.item.name"],
            Category: Config.Settings.Category ?? DefaultCategory,
            Price: Config.Decoy.Price,
            Team: team,
            Type: type,
            CanBeSold: false,
            SellPrice: null,
            IsEquipable: false,
            Duration: null,
            Enabled: Config.Decoy.Enabled,
            AllowPreview: false
        ));

        if (!itemRegistered)
        {
            Core.Logger.LogWarning("Failed to register decoy teleport item '{ItemId}'.", Config.Decoy.Id);
        }

        shopApi.OnItemPurchased += OnItemPurchased;
        shopApi.OnBeforeItemPurchase += OnBeforeItemPurchase;
        handlersRegistered = true;
    }

    private void OnBeforeItemPurchase(ShopBeforePurchaseContext context)
    {
        if (!IsTargetItem(context.Item.Id))
        {
            return;
        }

        var player = context.Player;
        var loc = Core.Translation.GetPlayerLocalizer(player);
        var prefix = loc["shop.prefix"];

        if (!IsPlayerAlive(player))
        {
            context.Block($"{prefix} {loc["decoy_teleport_module.error.must_be_alive"]}");
            return;
        }

        if (HasArmedDecoy(player.PlayerID))
        {
            context.Block($"{prefix} {loc["decoy_teleport_module.error.already_have_decoy"]}");
            return;
        }

        var remaining = GetCooldownRemainingSeconds(player.SteamID);
        if (remaining > 0)
        {
            context.Block($"{prefix} {loc["decoy_teleport_module.error.cooldown", remaining]}");
        }
    }

    private void OnItemPurchased(IPlayer player, ShopItemDefinition item)
    {
        if (player is null || !IsTargetItem(item.Id))
            return;

        Core.Scheduler.NextWorldUpdate(() =>
        {
            if (!IsPlayerAlive(player))
            {
                return;
            }

            var given = player.PlayerPawn?.ItemServices?.GiveItem<CBaseEntity>("weapon_decoy");
            if (given is null || !given.IsValid)
            {
                SendLocalized(player, "decoy_teleport_module.error.give_failed");
                return;
            }

            SetArmed(player.PlayerID, true);
            SendLocalized(player, "decoy_teleport_module.armed");
        });
    }

    [GameEventHandler(HookMode.Pre)]
    public HookResult OnDecoyStarted(EventDecoyStarted e)
    {
        IPlayer? thrower = e.UserIdPlayer;
        if (thrower is null || !thrower.IsValid || thrower.IsFakeClient)
            return HookResult.Continue;

        if (!TryConsumeArmed(thrower.PlayerID))
        {
            return HookResult.Continue;
        }

        var destination = new Vector(e.X, e.Y, e.Z + Config.Settings.TeleportZOffset);
        Core.Scheduler.NextWorldUpdate(() =>
        {
            if (!IsPlayerAlive(thrower))
            {
                return;
            }

            try
            {
                thrower.PlayerPawn?.Teleport(destination, null, Vector.Zero);
                ApplyCooldown(thrower.SteamID);
                SendLocalized(thrower, "decoy_teleport_module.teleported", Config.Settings.DecoyCooldown);
            }
            catch (Exception ex)
            {
                Core.Logger.LogWarning(ex, "Failed teleporting player {PlayerId} from decoy teleport module.", thrower.PlayerID);
            }
        });

        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Pre)]
    public HookResult OnPlayerDisconnect(EventPlayerDisconnect e)
    {
        IPlayer? player = e.UserIdPlayer;
        if (player is null)
            return HookResult.Continue;

        SetArmed(player.PlayerID, false);

        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerDeath(EventPlayerDeath e)
    {
        IPlayer? player = e.UserIdPlayer;
        if (player is null || player.IsFakeClient || !player.IsValid)
            return HookResult.Continue;

        if (HasArmedDecoy(player.PlayerID))
        {
            SetArmed(player.PlayerID, false);
            SendLocalized(player, "decoy_teleport_module.disarmed_on_death");
        }

        return HookResult.Continue;
    }
    [GameEventHandler(HookMode.Post)]
    public HookResult OnRoundStart(EventRoundStart e)
    {
        lock (stateSync)
        {
            armedPlayers.Clear();
        }

        return HookResult.Continue;
    }

    public override void Unload()
    {
        if (shopApi is not null)
        {
            shopApi.OnItemPurchased -= OnItemPurchased;
            shopApi.OnBeforeItemPurchase -= OnBeforeItemPurchase;

            if (!string.IsNullOrWhiteSpace(Config.Decoy.Id))
            {
                _ = shopApi.UnregisterItem(Config.Decoy.Id);
            }
        }

        lock (stateSync)
        {
            armedPlayers.Clear();
            cooldownUntilUnixSeconds.Clear();
        }

        handlersRegistered = false;
        itemRegistered = false;
    }

    private static bool TryParseTeam(string? value, out ShopItemTeam team)
    {
        if (Enum.TryParse(value, true, out team))
        {
            return true;
        }

        team = ShopItemTeam.Any;
        return false;
    }

    private static bool TryParseItemType(string? value, out ShopItemType type)
    {
        if (Enum.TryParse(value, true, out type))
        {
            return true;
        }

        type = ShopItemType.Consumable;
        return false;
    }

    private bool IsTargetItem(string itemId)
    {
        return itemId.Equals(Config.Decoy.Id, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPlayerAlive(IPlayer player)
    {
        var pawn = player.PlayerPawn;
        return player.IsValid && !player.IsFakeClient && pawn is not null && pawn.IsValid && pawn.LifeState == (int)LifeState_t.LIFE_ALIVE;
    }

    private bool HasArmedDecoy(int playerId)
    {
        lock (stateSync)
        {
            return armedPlayers.Contains(playerId);
        }
    }

    private void SetArmed(int playerId, bool armed)
    {
        lock (stateSync)
        {
            if (armed)
            {
                _ = armedPlayers.Add(playerId);
            }
            else
            {
                _ = armedPlayers.Remove(playerId);
            }
        }
    }

    private bool TryConsumeArmed(int playerId)
    {
        lock (stateSync)
        {
            return armedPlayers.Remove(playerId);
        }
    }

    private int GetCooldownRemainingSeconds(ulong steamId)
    {
        if (steamId == 0)
        {
            return 0;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        lock (stateSync)
        {
            if (!cooldownUntilUnixSeconds.TryGetValue(steamId, out var until))
            {
                return 0;
            }

            if (until <= now)
            {
                cooldownUntilUnixSeconds.Remove(steamId);
                return 0;
            }

            return (int)(until - now);
        }
    }

    private void ApplyCooldown(ulong steamId)
    {
        if (steamId == 0 || Config.Settings.DecoyCooldown <= 0)
        {
            return;
        }

        var until = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + Config.Settings.DecoyCooldown;
        lock (stateSync)
        {
            cooldownUntilUnixSeconds[steamId] = until;
        }
    }

    private void SendLocalized(IPlayer player, string key, params object[] args)
    {
        Core.Scheduler.NextWorldUpdate(() =>
        {
            if (player is null || !player.IsValid)
            {
                return;
            }

            var loc = Core.Translation.GetPlayerLocalizer(player);
            player.SendChat($"{loc["shop.prefix"]} {loc[key, args]}");
        });
    }

    private static void NormalizeConfig(DecoyModuleConfig config)
    {
        config.Settings ??= new DecoyModuleSettings();
        config.Decoy ??= new DecoyTeleportItemTemplate();

        if (string.IsNullOrWhiteSpace(config.Settings.Category))
        {
            config.Settings.Category = DefaultCategory;
        }

        if (config.Settings.DecoyCooldown < 0)
        {
            config.Settings.DecoyCooldown = 0;
        }

        if (config.Settings.TeleportZOffset < 0f)
        {
            config.Settings.TeleportZOffset = DefaultTeleportZOffset;
        }

        if (string.IsNullOrWhiteSpace(config.Decoy.Id))
        {
            config.Decoy.Id = "decoy_teleport";
        }

        if (config.Decoy.Price < 0)
        {
            config.Decoy.Price = 0;
        }

        if (string.IsNullOrWhiteSpace(config.Decoy.Type))
        {
            config.Decoy.Type = nameof(ShopItemType.Consumable);
        }

        if (string.IsNullOrWhiteSpace(config.Decoy.Team))
        {
            config.Decoy.Team = nameof(ShopItemTeam.Any);
        }
    }
}

internal sealed class DecoyModuleConfig
{
    public DecoyModuleSettings Settings { get; set; } = new();
    public DecoyTeleportItemTemplate Decoy { get; set; } = new();
}

internal sealed class DecoyModuleSettings
{
    public string Category { get; set; } = "Abilities";
    public int DecoyCooldown { get; set; } = 30;
    public float TeleportZOffset { get; set; } = 8f;
}

internal sealed class DecoyTeleportItemTemplate
{
    public string Id { get; set; } = "decoy_teleport";
    public int Price { get; set; } = 3000;
    public int? SellPrice { get; set; }
    public int DurationSeconds { get; set; } = 0;
    public string Type { get; set; } = nameof(ShopItemType.Consumable);
    public string Team { get; set; } = nameof(ShopItemTeam.Any);
    public bool Enabled { get; set; } = true;
    public bool CanBeSold { get; set; } = false;
}

