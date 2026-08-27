using Microsoft.Extensions.DependencyInjection;
using SwiftlyS2.Shared.Plugins;
using SwiftlyS2.Shared;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ShopCore.Contract;

using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameEvents;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;

namespace ShopCore;

[PluginMetadata(
    Id = "Shop_CustomWeapon",
    Version = "1.0.0",
    Name = "Shop Custom Weapon",
    Author = "aga",
    Description = "Allows players to purchase custom weapon variants from ShopCore"
)]
public sealed partial class Shop_CustomWeapon : BasePlugin
{
    private const string ShopCoreInterfaceKey = "ShopCore.API.v2";
    private const string ModulePluginId = "Shop_CustomWeapon";
    private const string ConfigFileName = "customweapon_items.jsonc";
    private const string ConfigSectionName = "Main";
    private const string DefaultCategory = "Weapons/Custom";

    private readonly HashSet<string> registeredItemIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CustomWeaponRuntime> runtimeByItemId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<nint, string> originalSubclassByWeaponAddress = new();
    private readonly Dictionary<nint, string> originalModelByWeaponAddress = new();
    private readonly Dictionary<nint, string> originalNameByWeaponAddress = new();
    private readonly Dictionary<int, uint> previewEntityIndexByPlayerId = new();
    private readonly object previewSync = new();

    private readonly HashSet<string> earlyPrecacheModels = new(StringComparer.OrdinalIgnoreCase);

    private IShopCoreApiV2? shopApi;
    private bool handlersRegistered;
    private CustomWeaponModuleSettings runtimeSettings = new();

    public Shop_CustomWeapon(ISwiftlyCore core) : base(core)
    {
    }

    public override void UseSharedInterface(IInterfaceManager interfaceManager)
    {
        shopApi = ResolveSharedInterface<IShopCoreApiV2>(interfaceManager, ShopCoreInterfaceKey);
    }

    public override void OnSharedInterfaceInjected(IInterfaceManager interfaceManager)
    {
        if (shopApi is null)
        {
            shopApi = ResolveSharedInterface<IShopCoreApiV2>(interfaceManager, ShopCoreInterfaceKey);
        }

        if (!handlersRegistered)
        {
            RegisterItemsAndHandlers();
        }
    }

    public override void Load(bool hotReload)
    {
        LoadEarlyPrecacheModels();
        Core.Event.OnPrecacheResource += OnPrecacheResource;
        Core.Event.OnClientConnected += OnClientConnected;
        Core.Event.OnEntityCreated += OnEntityCreated;

        if (shopApi is not null && !handlersRegistered)
        {
            RegisterItemsAndHandlers();
        }
    }

    public override void Unload()
    {
        Core.Event.OnPrecacheResource -= OnPrecacheResource;
        Core.Event.OnClientConnected -= OnClientConnected;
        Core.Event.OnEntityCreated -= OnEntityCreated;
        UnregisterItemsAndHandlers();
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnRoundStart(EventRoundStart @event)
    {
        if (!handlersRegistered || shopApi is null)
        {
            return HookResult.Continue;
        }

        _ = Core.Scheduler.DelayBySeconds(0.25f, ApplyEnabledWeaponsToAllPlayers);
        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerSpawn(EventPlayerSpawn @event)
    {
        if (!handlersRegistered || shopApi is null)
        {
            return HookResult.Continue;
        }

        var player = Core.PlayerManager.GetPlayer(@event.UserId);
        if (player is null || !player.IsValid || player.IsFakeClient)
        {
            return HookResult.Continue;
        }

        _ = Core.Scheduler.DelayBySeconds(0.25f, () => TryApplyEnabledWeaponsWithRetry(player, 8));
        return HookResult.Continue;
    }

    private void TryApplyEnabledWeaponsWithRetry(IPlayer player, int remainingAttempts)
    {
        if (!handlersRegistered || shopApi is null || !player.IsValid || player.IsFakeClient)
        {
            return;
        }

        if (!WeaponHelpers.IsPlayerAlive(player))
        {
            if (remainingAttempts > 0)
            {
                _ = Core.Scheduler.DelayBySeconds(0.15f, () => TryApplyEnabledWeaponsWithRetry(player, remainingAttempts - 1));
            }

            return;
        }

        ApplyEnabledWeaponsToPlayer(player);
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerTeam(EventPlayerTeam @event)
    {
        if (!handlersRegistered || shopApi is null)
        {
            return HookResult.Continue;
        }

        if (@event.Disconnect || @event.IsBot)
        {
            return HookResult.Continue;
        }

        var player = Core.PlayerManager.GetPlayer(@event.UserId);
        if (player is null || !player.IsValid || player.IsFakeClient)
        {
            return HookResult.Continue;
        }

        _ = Core.Scheduler.DelayBySeconds(0.5f, () => ApplyEnabledWeaponsToPlayer(player));
        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnItemPurchase(EventItemPurchase @event)
    {
        if (!handlersRegistered || shopApi is null)
        {
            return HookResult.Continue;
        }

        var player = @event.UserIdPlayer;
        if (player is null || !player.IsValid || player.IsFakeClient)
        {
            return HookResult.Continue;
        }

        var purchasedWeapon = @event.Weapon;
        if (string.IsNullOrWhiteSpace(purchasedWeapon))
        {
            return HookResult.Continue;
        }

        // Check if player has any enabled custom weapon items for this base weapon
        foreach (var itemId in registeredItemIds)
        {
            if (!shopApi.IsItemEnabled(player, itemId))
            {
                continue;
            }

            if (!TryGetRuntime(itemId, out var runtime))
            {
                continue;
            }

            if (!string.Equals(runtime.BaseWeapon, purchasedWeapon, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Delay to let the engine finish creating the weapon entity
            Core.Scheduler.NextWorldUpdate(() =>
            {
                if (!WeaponHelpers.IsPlayerAlive(player))
                {
                    return;
                }

                var weapon = WeaponHelpers.FindWeapon(player, runtime.BaseWeapon);
                if (weapon is null || !weapon.IsValid)
                {
                    return;
                }

                ApplyAppearance(weapon, runtime);
            });

            break;
        }

        return HookResult.Continue;
    }

    [Command("cw_debug", registerRaw: true)]
    public void DebugCommand(ICommandContext context)
    {
        Core.Logger.LogInformation("[CW_DEBUG] RuntimeItems={Count}, EarlyPrecache={Early}, Registered={Reg}",
            runtimeByItemId.Count, earlyPrecacheModels.Count, registeredItemIds.Count);

        foreach (var model in earlyPrecacheModels)
        {
            Core.Logger.LogInformation("[CW_DEBUG] EarlyPrecacheModel: {Model}", model);
        }

        foreach (var runtime in runtimeByItemId.Values)
        {
            Core.Logger.LogInformation("[CW_DEBUG] Item: {ItemId} | Base={Base} | Vdata={Vdata} | Model={Model}",
                runtime.ItemId, runtime.BaseWeapon, runtime.VdataName, runtime.PrecacheModel);
        }

        foreach (var player in Core.PlayerManager.GetAlive())
        {
            var weaponServices = player.PlayerPawn?.WeaponServices;
            if (weaponServices is null || !weaponServices.IsValid)
            {
                continue;
            }

            foreach (var weapon in weaponServices.MyValidWeapons)
            {
                var name = weapon.Entity?.DesignerName ?? "unknown";
                var model = weapon.CBodyComponent?.SceneNode?.GetSkeletonInstance().ModelState.ModelName ?? "none";
                Core.Logger.LogInformation("[CW_DEBUG] Player {Pid} Weapon: {Name} | CurrentModel={Model}",
                    player.PlayerID, name, model);
            }
        }
    }

    private void OnClientConnected(IOnClientConnectedEvent @event)
    {
        if (!handlersRegistered || shopApi is null)
        {
            return;
        }

        _ = Core.Scheduler.DelayBySeconds(1.5f, () =>
        {
            var player = Core.PlayerManager.GetPlayer(@event.PlayerId);
            if (player is null || !player.IsValid || player.IsFakeClient)
            {
                return;
            }

            if (!WeaponHelpers.IsPlayerAlive(player))
            {
                return;
            }

            ApplyEnabledWeaponsToPlayer(player);
        });
    }

    private void OnPrecacheResource(IOnPrecacheResourceEvent @event)
    {
        var precached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Precache from runtime items (populated after ShopCore API is available)
        foreach (var runtime in runtimeByItemId.Values)
        {
            if (!string.IsNullOrWhiteSpace(runtime.PrecacheModel) && precached.Add(runtime.PrecacheModel))
            {
                Core.Logger.LogInformation("[Shop_CustomWeapon] Precaching: {ModelPath}", runtime.PrecacheModel);
                @event.AddItem(runtime.PrecacheModel);
            }
        }

        // Also precache from early-cached models (loaded from config in Load())
        foreach (var modelPath in earlyPrecacheModels)
        {
            if (precached.Add(modelPath))
            {
                Core.Logger.LogInformation("[Shop_CustomWeapon] Early precaching: {ModelPath}", modelPath);
                @event.AddItem(modelPath);
            }
        }

        Core.Logger.LogInformation(
            "[Shop_CustomWeapon] OnPrecacheResource done. Total models precached: {Count}",
            precached.Count
        );
    }
}