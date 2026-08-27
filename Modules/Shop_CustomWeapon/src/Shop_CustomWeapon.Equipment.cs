using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using ShopCore.Contract;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;

namespace ShopCore;

public sealed partial class Shop_CustomWeapon
{
    private const float PreviewDurationSeconds = 8f;
    private const float PreviewDistance = 75f;
    private const int KnifeApplyRetryAttempts = 20;
    private const float KnifeApplyRetryDelaySeconds = 0.1f;

    private void OnEntityCreated(IOnEntityCreatedEvent @event)
    {
        if (!handlersRegistered || shopApi is null)
        {
            return;
        }

        var entity = @event.Entity;
        if (entity is null || !entity.IsValid)
        {
            return;
        }

        var designerName = entity.Entity?.DesignerName ?? string.Empty;
        if (!WeaponHelpers.IsKnifeWeapon(designerName))
        {
            return;
        }

        Core.Scheduler.NextWorldUpdate(() =>
        {
            try
            {
                if (entity is null || !entity.IsValid)
                {
                    return;
                }

                var weapon = entity.As<CBasePlayerWeapon>();
                if (weapon is null || !weapon.IsValid)
                {
                    return;
                }

                var ownerHandle = weapon.OwnerEntity;
                if (!ownerHandle.IsValid)
                {
                    return;
                }

                var ownerEntity = ownerHandle.Value;
                if (ownerEntity is null || !ownerEntity.IsValid)
                {
                    return;
                }

                var pawn = ownerEntity.As<CCSPlayerPawn>();
                if (pawn is null || !pawn.IsValid)
                {
                    return;
                }

                var controller = pawn.OriginalController;
                if (!controller.IsValid)
                {
                    return;
                }

                var controllerValue = controller.Value;
                if (controllerValue is null || !controllerValue.IsValid)
                {
                    return;
                }

                var player = Core.PlayerManager.GetPlayer((int)(controllerValue.Index - 1));
                if (player is null || !player.IsValid || player.IsFakeClient)
                {
                    return;
                }

                CustomWeaponRuntime? matchedRuntime = null;
                var appliedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

                    if (!WeaponHelpers.IsKnifeWeapon(runtime.BaseWeapon))
                    {
                        continue;
                    }

                    var equipGroup = GetExclusiveEquipGroup(runtime);
                    if (!appliedGroups.Add(equipGroup))
                    {
                        continue;
                    }

                    matchedRuntime = runtime;
                    break;
                }

                if (matchedRuntime is null)
                {
                    return;
                }

                if (WeaponHelpers.KnifeDefinitionIndexByClassname.TryGetValue(matchedRuntime.BaseWeapon, out var defIndex))
                {
                    weapon.AttributeManager.Item.ItemDefinitionIndex = defIndex;
                }

                ApplyAppearance(weapon, matchedRuntime);
            }
            catch (Exception ex)
            {
                Core.Logger.LogWarning(ex, "[Shop_CustomWeapon] OnEntityCreated knife skin failed.");
            }
        });
    }

    private void OnItemPurchased(IPlayer player, ShopItemDefinition item)
    {
        if (!TryGetRuntime(item.Id, out var runtime))
        {
            return;
        }

        EnforceExclusiveEquip(player, item.Id, runtime);

        if (WeaponHelpers.IsKnifeWeapon(runtime.BaseWeapon))
        {
            ApplyKnifeSkin(player, runtime);
            return;
        }

        GiveOrApplyWeapon(player, runtime, runtime.SelectWeaponOnEquip, sendFailureMessage: false);
    }

    private void OnItemToggled(IPlayer player, ShopItemDefinition item, bool enabled)
    {
        if (!TryGetRuntime(item.Id, out var runtime))
        {
            return;
        }

        if (enabled)
        {
            EnforceExclusiveEquip(player, item.Id, runtime);

            if (WeaponHelpers.IsKnifeWeapon(runtime.BaseWeapon))
            {
                ApplyKnifeSkin(player, runtime);
                return;
            }

            GiveOrApplyWeapon(player, runtime, runtime.SelectWeaponOnEquip, sendFailureMessage: false);
            return;
        }

        if (WeaponHelpers.IsKnifeWeapon(runtime.BaseWeapon))
        {
            ResetKnife(player, runtime);
            return;
        }

        ResetWeaponForPlayer(player, runtime);
    }

    private void OnItemSold(IPlayer player, ShopItemDefinition item, decimal _)
    {
        if (!TryGetRuntime(item.Id, out var runtime))
        {
            return;
        }

        if (WeaponHelpers.IsKnifeWeapon(runtime.BaseWeapon))
        {
            ResetKnife(player, runtime);
            return;
        }

        ResetWeaponForPlayer(player, runtime);
    }

    private void OnItemExpired(IPlayer player, ShopItemDefinition item)
    {
        if (!TryGetRuntime(item.Id, out var runtime))
        {
            return;
        }

        if (WeaponHelpers.IsKnifeWeapon(runtime.BaseWeapon))
        {
            ResetKnife(player, runtime);
            return;
        }

        ResetWeaponForPlayer(player, runtime);
    }

    private void OnItemPreview(IPlayer player, ShopItemDefinition item)
    {
        if (!TryGetRuntime(item.Id, out var runtime))
        {
            return;
        }

        if (WeaponHelpers.IsKnifeWeapon(runtime.BaseWeapon))
        {
            SpawnWeaponPreview(player, runtime, item.DisplayName);
            return;
        }

        SpawnWeaponPreview(player, runtime, item.DisplayName);
    }

    private void ApplyEnabledWeaponsToAllPlayers()
    {
        if (shopApi is null)
        {
            return;
        }

        foreach (var player in Core.PlayerManager.GetAlive())
        {
            ApplyEnabledWeaponsToPlayer(player, onlyGrantOnRoundStart: true);
        }
    }

    private void SpawnWeaponPreview(IPlayer player, CustomWeaponRuntime runtime, string displayName)
    {
        var previewModel = !string.IsNullOrWhiteSpace(runtime.PrecacheModel)
            ? runtime.PrecacheModel
            : string.Empty;

        if (string.IsNullOrWhiteSpace(previewModel))
        {
            if (string.IsNullOrWhiteSpace(runtime.VdataName))
            {
                return;
            }

            SpawnVDataPreview(player, runtime, displayName);
            return;
        }

        Core.Scheduler.NextWorldUpdate(() =>
        {
            if (!TryGetAlivePawn(player, out var pawn))
            {
                return;
            }

            var origin = pawn.AbsOrigin ?? Vector.Zero;
            var rotation = pawn.AbsRotation ?? QAngle.Zero;
            var yawRadians = rotation.Y * (MathF.PI / 180f);
            var previewPosition = new Vector(
                origin.X + (MathF.Cos(yawRadians) * 96f),
                origin.Y + (MathF.Sin(yawRadians) * 96f),
                origin.Z + 8f
            );
            var previewRotation = new QAngle(rotation.X, NormalizeYaw(rotation.Y + 180f), rotation.Z);

            CDynamicProp? preview = null;
            try
            {
                preview = Core.EntitySystem.CreateEntityByDesignerName<CDynamicProp>("prop_dynamic_override");
                if (preview is null || !preview.IsValid)
                {
                    return;
                }

                preview.Teleport(previewPosition, previewRotation, Vector.Zero);
                preview.DispatchSpawn();
                preview.SetModel(previewModel);
                ConfigurePreviewVisibility(preview, player);

                var playerId = player.PlayerID;
                lock (previewSync)
                {
                    if (previewEntityIndexByPlayerId.TryGetValue(playerId, out var oldIndex) && oldIndex != 0)
                    {
                        DespawnPreviewEntityByIndex(oldIndex);
                    }

                    previewEntityIndexByPlayerId[playerId] = preview.Index;
                }

                player.SendChat($"{GetPrefix(player)} {Core.Translation.GetPlayerLocalizer(player)["preview.started", displayName, (int)PreviewDurationSeconds]}");
            }
            catch (Exception ex)
            {
                Core.Logger.LogWarning(ex, "Failed to spawn custom weapon preview for '{DisplayName}'.", displayName);
                if (preview is not null && preview.IsValid)
                {
                    try
                    {
                        preview.Despawn();
                    }
                    catch
                    {
                    }
                }
                return;
            }

            _ = Core.Scheduler.DelayBySeconds(PreviewDurationSeconds, () =>
            {
                Core.Scheduler.NextWorldUpdate(() =>
                {
                    if (!player.IsValid)
                    {
                        return;
                    }

                    lock (previewSync)
                    {
                        if (!previewEntityIndexByPlayerId.TryGetValue(player.PlayerID, out var currentIndex) || currentIndex != preview.Index)
                        {
                            return;
                        }

                        previewEntityIndexByPlayerId.Remove(player.PlayerID);
                    }

                    try
                    {
                        if (preview is not null && preview.IsValid)
                        {
                            preview.Despawn();
                        }
                    }
                    catch (Exception ex)
                    {
                        Core.Logger.LogWarning(ex, "Failed to despawn custom weapon preview.");
                    }
                });
            });
        });
    }

    private void SpawnVDataPreview(IPlayer player, CustomWeaponRuntime runtime, string displayName)
    {
        Core.Scheduler.NextWorldUpdate(() =>
        {
            if (!WeaponHelpers.IsPlayerAlive(player))
            {
                return;
            }

            var pawn = player.PlayerPawn;
            var weaponServices = pawn?.WeaponServices;
            if (pawn is null || weaponServices is null || !weaponServices.IsValid)
            {
                return;
            }

            var isKnife = WeaponHelpers.IsKnifeWeapon(runtime.BaseWeapon);
            var prevActive = weaponServices.ActiveWeapon;

            var weapon = isKnife
                ? WeaponHelpers.FindPlayerKnife(player)
                : WeaponHelpers.FindWeapon(player, runtime.BaseWeapon);

            if (weapon is null || !weapon.IsValid)
            {
                player.SendChat($"{GetPrefix(player)} Equip a {displayName} first to preview this skin.");
                return;
            }

            var capturedWeapon = weapon;

            Core.Scheduler.NextWorldUpdate(() =>
            {
                if (!WeaponHelpers.IsPlayerAlive(player) || !capturedWeapon.IsValid)
                {
                    return;
                }

                var ws = player.PlayerPawn?.WeaponServices;
                if (ws is null || !ws.IsValid)
                {
                    return;
                }

                ApplyAppearance(capturedWeapon, runtime);
                ws.SelectWeapon(capturedWeapon);

                player.SendChat($"{GetPrefix(player)} {Core.Translation.GetPlayerLocalizer(player)["preview.started", displayName, (int)PreviewDurationSeconds]}");

                _ = Core.Scheduler.DelayBySeconds(PreviewDurationSeconds, () =>
                {
                    Core.Scheduler.NextWorldUpdate(() =>
                    {
                        if (!player.IsValid)
                        {
                            return;
                        }

                        var cleanWs = player.PlayerPawn?.WeaponServices;
                        if (prevActive.IsValid && prevActive.Value is { } prev && cleanWs is not null && cleanWs.IsValid)
                        {
                            cleanWs.SelectWeapon(prev);
                        }

                        ApplyEnabledWeaponsToPlayer(player);
                    });
                });
            });
        });
    }

    private void ConfigurePreviewVisibility(CDynamicProp preview, IPlayer player)
    {
        try
        {
            preview.RenderMode = RenderMode_t.kRenderNormal;
        }
        catch
        {
        }
    }

    private static bool TryGetAlivePawn(IPlayer player, out CCSPlayerPawn pawn)
    {
        pawn = default!;

        if (player is null || !player.IsValid || player.IsFakeClient)
        {
            return false;
        }

        var candidate = player.PlayerPawn;
        if (candidate is null || !candidate.IsValid)
        {
            return false;
        }

        pawn = candidate;
        return pawn.LifeState == (int)LifeState_t.LIFE_ALIVE;
    }

    private static float NormalizeYaw(float yaw)
    {
        var normalized = yaw % 360f;
        return normalized < 0f ? normalized + 360f : normalized;
    }

    private void DespawnPreviewEntityByIndex(uint entityIndex)
    {
        if (entityIndex == 0)
        {
            return;
        }

        var entity = Core.EntitySystem.GetEntityByIndex<CEntityInstance>(entityIndex);
        if (entity?.IsValid is not true)
        {
            return;
        }

        try
        {
            entity.Despawn();
        }
        catch
        {
        }
    }

    private void ApplyEnabledWeaponsToPlayer(IPlayer player, bool onlyGrantOnRoundStart = false)
    {
        if (shopApi is null || !WeaponHelpers.IsPlayerAlive(player))
        {
            return;
        }

        var appliedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

            if (onlyGrantOnRoundStart && !runtime.GrantOnRoundStart)
            {
                continue;
            }

            var equipGroup = GetExclusiveEquipGroup(runtime);
            if (!appliedGroups.Add(equipGroup))
            {
                continue;
            }

            if (WeaponHelpers.IsKnifeWeapon(runtime.BaseWeapon))
            {
                ApplyKnifeSkin(player, runtime);
            }
            else
            {
                ApplyToExistingWeapon(player, runtime);
            }
        }
    }

    private void ApplyToExistingWeapon(IPlayer player, CustomWeaponRuntime runtime)
    {
        if (!WeaponHelpers.IsPlayerAlive(player))
        {
            return;
        }

        if (WeaponHelpers.IsThrowableWeapon(runtime.BaseWeapon))
        {
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

            return;
        }

        ReplaceWeaponForPlayer(player, runtime, forceSelectWeapon: true, sendFailureMessage: false);
    }

    private bool GiveOrApplyWeapon(IPlayer player, CustomWeaponRuntime runtime, bool selectWeapon, bool sendFailureMessage)
    {
        if (!WeaponHelpers.IsPlayerAlive(player))
        {
            return false;
        }

        Core.Scheduler.NextWorldUpdate(() =>
        {
            try
            {
                if (!WeaponHelpers.IsPlayerAlive(player))
                {
                    return;
                }

                var weaponServices = player.PlayerPawn?.WeaponServices;
                var itemServices = player.PlayerPawn?.ItemServices;
                if (weaponServices is null || !weaponServices.IsValid || itemServices is null || !itemServices.IsValid)
                {
                    return;
                }

                var weapon = WeaponHelpers.FindWeapon(player, runtime.BaseWeapon);
                if (weapon is not null && weapon.IsValid)
                {
                    if (WeaponHelpers.IsThrowableWeapon(runtime.BaseWeapon))
                    {
                        ApplyAppearance(weapon, runtime);

                        if (selectWeapon)
                        {
                            weaponServices.SelectWeapon(weapon);
                        }

                        return;
                    }

                    ReplaceWeaponForPlayer(player, runtime, forceSelectWeapon: selectWeapon, sendFailureMessage: sendFailureMessage);
                    return;
                }

                if (WeaponHelpers.IsThrowableWeapon(runtime.BaseWeapon))
                {
                    return;
                }

                weapon = itemServices.GiveItem<CBasePlayerWeapon>(runtime.BaseWeapon);

                if (weapon is null || !weapon.IsValid)
                {
                    if (sendFailureMessage)
                    {
                        player.SendChat($"{GetPrefix(player)} Failed to give {runtime.DisplayName}.");
                    }
                    return;
                }

                var capturedWeapon = weapon;
                Core.Scheduler.NextWorldUpdate(() =>
                {
                    if (!WeaponHelpers.IsPlayerAlive(player) || !capturedWeapon.IsValid)
                    {
                        return;
                    }

                    var ws2 = player.PlayerPawn?.WeaponServices;
                    if (ws2 is null || !ws2.IsValid)
                    {
                        return;
                    }

                    ApplyAppearance(capturedWeapon, runtime);

                    if (selectWeapon)
                    {
                        ws2.SelectWeapon(capturedWeapon);
                    }
                });
            }
            catch (Exception ex)
            {
                Core.Logger.LogWarning(ex, "Failed to apply custom weapon '{ItemId}' to player {PlayerId}.", runtime.ItemId, player.PlayerID);
            }
        });

        return true;
    }

    private void ReplaceWeaponForPlayer(IPlayer player, CustomWeaponRuntime runtime, bool forceSelectWeapon, bool sendFailureMessage)
    {
        if (!WeaponHelpers.IsPlayerAlive(player))
        {
            return;
        }

        Core.Scheduler.NextWorldUpdate(() =>
        {
            try
            {
                if (!WeaponHelpers.IsPlayerAlive(player))
                {
                    return;
                }

                var pawn = player.PlayerPawn;
                var weaponServices = pawn?.WeaponServices;
                var itemServices = pawn?.ItemServices;
                if (weaponServices is null || !weaponServices.IsValid || itemServices is null || !itemServices.IsValid)
                {
                    return;
                }

                var existingWeapon = WeaponHelpers.FindWeapon(player, runtime.BaseWeapon);
                if (existingWeapon is null || !existingWeapon.IsValid)
                {
                    return;
                }

                var activeWeapon = weaponServices.ActiveWeapon;
                var wasSelected = activeWeapon.IsValid && activeWeapon.Value == existingWeapon;
                var clip1 = existingWeapon.Clip1;
                var clip2 = existingWeapon.Clip2;

                weaponServices.RemoveWeapon(existingWeapon);

                var replacementWeapon = itemServices.GiveItem<CBasePlayerWeapon>(runtime.BaseWeapon);
                if (replacementWeapon is null || !replacementWeapon.IsValid)
                {
                    if (sendFailureMessage)
                    {
                        player.SendChat($"{GetPrefix(player)} Failed to rebuild {runtime.DisplayName}.");
                    }
                    return;
                }

                var shouldSelect = forceSelectWeapon || wasSelected;
                Core.Scheduler.NextWorldUpdate(() =>
                {
                    if (!WeaponHelpers.IsPlayerAlive(player) || !replacementWeapon.IsValid)
                    {
                        return;
                    }

                    replacementWeapon.Clip1 = clip1;
                    replacementWeapon.Clip2 = clip2;
                    ApplyAppearance(replacementWeapon, runtime);

                    var ws2 = player.PlayerPawn?.WeaponServices;
                    if (shouldSelect && ws2 is not null && ws2.IsValid)
                    {
                        ws2.SelectWeapon(replacementWeapon);
                    }
                });
            }
            catch (Exception ex)
            {
                Core.Logger.LogWarning(ex, "Failed to replace custom weapon '{ItemId}' for player {PlayerId}.", runtime.ItemId, player.PlayerID);
            }
        });
    }

    private void RebuildBaseWeaponForPlayer(IPlayer player, CustomWeaponRuntime runtime)
    {
        if (!WeaponHelpers.IsPlayerAlive(player))
        {
            return;
        }

        if (WeaponHelpers.IsThrowableWeapon(runtime.BaseWeapon))
        {
            return;
        }

        Core.Scheduler.NextWorldUpdate(() =>
        {
            try
            {
                if (!WeaponHelpers.IsPlayerAlive(player))
                {
                    return;
                }

                var pawn = player.PlayerPawn;
                var weaponServices = pawn?.WeaponServices;
                var itemServices = pawn?.ItemServices;
                if (weaponServices is null || !weaponServices.IsValid || itemServices is null || !itemServices.IsValid)
                {
                    return;
                }

                var weapon = WeaponHelpers.FindWeapon(player, runtime.BaseWeapon);
                if (weapon is null || !weapon.IsValid)
                {
                    return;
                }

                var activeWeapon = weaponServices.ActiveWeapon;
                var wasSelected = activeWeapon.IsValid && activeWeapon.Value == weapon;
                var clip1 = weapon.Clip1;
                var clip2 = weapon.Clip2;

                weaponServices.RemoveWeapon(weapon);

                var cleanWeapon = itemServices.GiveItem<CBasePlayerWeapon>(runtime.BaseWeapon);
                if (cleanWeapon is null || !cleanWeapon.IsValid)
                {
                    return;
                }

                Core.Scheduler.NextWorldUpdate(() =>
                {
                    if (!WeaponHelpers.IsPlayerAlive(player) || !cleanWeapon.IsValid)
                    {
                        return;
                    }

                    cleanWeapon.Clip1 = clip1;
                    cleanWeapon.Clip2 = clip2;

                    var ws2 = player.PlayerPawn?.WeaponServices;
                    if (wasSelected && ws2 is not null && ws2.IsValid)
                    {
                        ws2.SelectWeapon(cleanWeapon);
                    }
                });
            }
            catch (Exception ex)
            {
                Core.Logger.LogWarning(ex, "Failed to rebuild base weapon '{ItemId}' for player {PlayerId}.", runtime.ItemId, player.PlayerID);
            }
        });
    }

    private void ResetWeaponForPlayer(IPlayer player, CustomWeaponRuntime runtime)
    {
        if (!player.IsValid || player.IsFakeClient)
        {
            return;
        }

        Core.Scheduler.NextWorldUpdate(() =>
        {
            try
            {
                if (!WeaponHelpers.IsPlayerAlive(player))
                {
                    return;
                }

                RebuildBaseWeaponForPlayer(player, runtime);
            }
            catch (Exception ex)
            {
                Core.Logger.LogWarning(ex, "Failed to reset custom weapon '{ItemId}' for player {PlayerId}.", runtime.ItemId, player.PlayerID);
            }
        });
    }

    private void ApplyKnifeSkin(IPlayer player, CustomWeaponRuntime runtime)
    {
        ApplyKnifeSkin(player, runtime, KnifeApplyRetryAttempts);
    }

    private void ApplyKnifeSkin(IPlayer player, CustomWeaponRuntime runtime, int remainingAttempts)
    {
        if (!WeaponHelpers.IsPlayerAlive(player))
        {
            if (remainingAttempts > 0)
            {
                _ = Core.Scheduler.DelayBySeconds(KnifeApplyRetryDelaySeconds, () => ApplyKnifeSkin(player, runtime, remainingAttempts - 1));
            }
            return;
        }

        Core.Scheduler.NextWorldUpdate(() =>
        {
            try
            {
                if (!WeaponHelpers.IsPlayerAlive(player))
                {
                    return;
                }

                var knife = WeaponHelpers.FindPlayerKnife(player);

                if (knife is null || !knife.IsValid)
                {
                    var pawn = player.PlayerPawn;
                    var weaponServices = pawn?.WeaponServices;
                    if (weaponServices is null || !weaponServices.IsValid)
                    {
                        if (remainingAttempts > 0)
                            _ = Core.Scheduler.DelayBySeconds(KnifeApplyRetryDelaySeconds, () => ApplyKnifeSkin(player, runtime, remainingAttempts - 1));
                        return;
                    }

                    var defaultKnifeClass = player.Controller?.TeamNum == (int)Team.T ? "weapon_knife_t" : "weapon_knife";
                    weaponServices.SelectWeaponByDesignerName(defaultKnifeClass);

                    Core.Scheduler.NextWorldUpdate(() =>
                    {
                        try
                        {
                            if (!WeaponHelpers.IsPlayerAlive(player)) return;

                            var ws2 = player.PlayerPawn?.WeaponServices;
                            if (ws2 is null || !ws2.IsValid)
                            {
                                if (remainingAttempts > 0)
                                    _ = Core.Scheduler.DelayBySeconds(KnifeApplyRetryDelaySeconds, () => ApplyKnifeSkin(player, runtime, remainingAttempts - 1));
                                return;
                            }

                            var activeHandle = ws2.ActiveWeapon;
                            var activeKnife = activeHandle.IsValid ? activeHandle.Value : null;

                            if (activeKnife is null || !activeKnife.IsValid)
                            {
                                if (remainingAttempts > 0)
                                    _ = Core.Scheduler.DelayBySeconds(KnifeApplyRetryDelaySeconds, () => ApplyKnifeSkin(player, runtime, remainingAttempts - 1));
                                return;
                            }

                            if (WeaponHelpers.KnifeDefinitionIndexByClassname.TryGetValue(runtime.BaseWeapon, out var defIdx))
                                activeKnife.AttributeManager.Item.ItemDefinitionIndex = defIdx;

                            ApplyAppearance(activeKnife, runtime);
                        }
                        catch (Exception ex2)
                        {
                            Core.Logger.LogWarning(ex2, "Failed to apply knife skin '{ItemId}' to player {PlayerId}.", runtime.ItemId, player.PlayerID);
                            if (remainingAttempts > 0)
                                _ = Core.Scheduler.DelayBySeconds(KnifeApplyRetryDelaySeconds, () => ApplyKnifeSkin(player, runtime, remainingAttempts - 1));
                        }
                    });
                    return;
                }

                if (WeaponHelpers.KnifeDefinitionIndexByClassname.TryGetValue(runtime.BaseWeapon, out var expectedKnifeDefIndex))
                {
                    knife.AttributeManager.Item.ItemDefinitionIndex = expectedKnifeDefIndex;
                }

                ApplyAppearance(knife, runtime);
            }
            catch (Exception ex)
            {
                Core.Logger.LogWarning(ex, "Failed to apply knife skin '{ItemId}' to player {PlayerId}.", runtime.ItemId, player.PlayerID);
                if (remainingAttempts > 0)
                {
                    _ = Core.Scheduler.DelayBySeconds(KnifeApplyRetryDelaySeconds, () => ApplyKnifeSkin(player, runtime, remainingAttempts - 1));
                }
            }
        });
    }

    private void ResetKnife(IPlayer player, CustomWeaponRuntime runtime)
    {
        if (!WeaponHelpers.IsPlayerAlive(player))
        {
            return;
        }

        Core.Scheduler.NextWorldUpdate(() =>
        {
            try
            {
                if (!WeaponHelpers.IsPlayerAlive(player))
                {
                    return;
                }

                var pawn = player.PlayerPawn;
                if (pawn is null || !pawn.IsValid)
                {
                    return;
                }

                var weaponServices = pawn.WeaponServices;
                var itemServices = pawn.ItemServices;
                if (weaponServices is null || !weaponServices.IsValid || itemServices is null || !itemServices.IsValid)
                {
                    return;
                }

                var knife = WeaponHelpers.FindPlayerKnife(player);
                if (knife is null || !knife.IsValid)
                {
                    return;
                }

                weaponServices.RemoveWeapon(knife);
                var defaultKnifeClass = player.Controller.TeamNum == (int)Team.T ? "weapon_knife_t" : "weapon_knife";
                itemServices.GiveItem<CBasePlayerWeapon>(defaultKnifeClass);
            }
            catch (Exception ex)
            {
                Core.Logger.LogWarning(ex, "Failed to reset knife for player {PlayerId}.", player.PlayerID);
            }
        });
    }

    private void EnforceExclusiveEquip(IPlayer player, string selectedItemId, CustomWeaponRuntime selectedRuntime)
    {
        if (shopApi is null)
        {
            return;
        }

        var selectedGroup = GetExclusiveEquipGroup(selectedRuntime);

        foreach (var otherItemId in registeredItemIds)
        {
            if (string.Equals(otherItemId, selectedItemId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryGetRuntime(otherItemId, out var otherRuntime))
            {
                continue;
            }

            if (!string.Equals(GetExclusiveEquipGroup(otherRuntime), selectedGroup, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!shopApi.IsItemEnabled(player, otherItemId))
            {
                continue;
            }

            _ = shopApi.SetItemEnabled(player, otherItemId, false);
        }
    }

    private static string GetExclusiveEquipGroup(CustomWeaponRuntime runtime)
    {
        if (WeaponHelpers.IsKnifeWeapon(runtime.BaseWeapon))
        {
            return "knife";
        }

        return string.IsNullOrWhiteSpace(runtime.BaseWeapon)
            ? string.Empty
            : runtime.BaseWeapon.Trim();
    }
}
