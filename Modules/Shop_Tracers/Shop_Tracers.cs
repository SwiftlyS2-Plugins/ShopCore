using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using ShopCore.Contract;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameEvents;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Plugins;
using SwiftlyS2.Shared.SchemaDefinitions;

using Color = SwiftlyS2.Shared.Natives.Color;
using SystemColor = System.Drawing.Color;
using ColorTranslator = System.Drawing.ColorTranslator;

namespace ShopCore;

#region Helper Models and Enums

public enum TracerColorMode
{
    Static,
    Random,
    Team
}

public sealed class TracersModuleConfig
{
    public TracersModuleSettings Settings { get; set; } = new();
    public List<TracerItemTemplate> Items { get; set; } = new();
}

public sealed class TracersModuleSettings
{
    public string Category { get; set; } = "Visuals/Tracers";
    public bool UseCorePrefix { get; set; } = true;
    public float MinDrawIntervalSeconds { get; set; } = 0.05f;
    public int PoolSizePerPlayer { get; set; } = 8;
    public float DefaultLifeSeconds { get; set; } = 0.4f;
    public float DefaultStartWidth { get; set; } = 2.0f;
    public float DefaultEndWidth { get; set; } = 1.0f;
    public float DefaultOriginZOffset { get; set; } = 57f;
}

public sealed class TracerItemTemplate
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string DisplayNameKey { get; set; } = string.Empty;
    public string ColorDisplayName { get; set; } = string.Empty;
    public string Color { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public decimal? SellPrice { get; set; }
    public int DurationSeconds { get; set; }
    public string Type { get; set; } = nameof(ShopItemType.Temporary);
    public string Team { get; set; } = nameof(ShopItemTeam.Any);
    public bool Enabled { get; set; } = true;
    public bool CanBeSold { get; set; } = true;
    public float? LifeSeconds { get; set; }
    public float? StartWidth { get; set; }
    public float? EndWidth { get; set; }
    public float? OriginZOffset { get; set; }
    public string? RequiredPermission { get; set; }
}

public readonly record struct TracerItemRuntime(
    string ItemId,
    TracerColorMode ColorMode,
    Color StaticColor,
    float LifeSeconds,
    float StartWidth,
    float EndWidth,
    float OriginZOffset,
    string RequiredPermission
);

public readonly record struct TracerPreviewState(
    TracerItemRuntime Runtime,
    float ExpiresAt
);

public readonly record struct CachedTracerRuntime(
    TracerItemRuntime Runtime,
    bool HasRuntime,
    float NextRefreshAt
);

public sealed class TracerBeamPool
{
    public CBeam?[] Slots { get; }
    public float[] HideAt { get; }
    public bool[] Hidden { get; }
    public int NextIndex { get; set; }

    public TracerBeamPool(int size)
    {
        Slots = new CBeam?[size];
        HideAt = new float[size];
        Hidden = new bool[size];
    }
}

#endregion

[PluginMetadata(
    Id = "Shop_Tracers",
    Name = "Shop Tracers",
    Author = "T3Marius",
    Version = "1.0.1",
    Description = "ShopCore module with bullet tracer items"
)]
public class Shop_Tracers : BasePlugin
{
    private const string ShopCoreInterfaceKey = "ShopCore.API.v2";
    private const string ModulePluginId = "Shop_Tracers";
    private const string TemplateFileName = "tracers_config.jsonc";
    private const string TemplateSectionName = "Main";
    private const string DefaultCategory = "Visuals/Tracers";
    private const float PreviewDurationSeconds = 12f;
    private const float BeamSweepIntervalSeconds = 0.05f;
    private const float ActiveRuntimeRefreshSeconds = 2.0f;

    private static readonly Color TeamTColor = new((byte)255, (byte)220, (byte)50, (byte)255);
    private static readonly Color TeamCtColor = new((byte)80, (byte)170, (byte)255, (byte)255);

    private IShopCoreApiV2? shopApi;
    private bool handlersRegistered;

    private readonly HashSet<string> registeredItemIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> registeredItemOrder = new();
    private readonly Dictionary<string, TracerItemRuntime> itemRuntimeById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, TracerPreviewState> previewRuntimeByPlayerId = new();
    private readonly Dictionary<int, CachedTracerRuntime> activeRuntimeByPlayerId = new();
    private readonly Dictionary<int, float> nextDrawAllowedAtByPlayerId = new();
    private readonly Dictionary<int, TracerBeamPool> beamPoolByPlayerId = new();
    private CancellationTokenSource? beamSweepTimer;
    private readonly Random random = new();

    private TracersModuleSettings runtimeSettings = new();

    public Shop_Tracers(ISwiftlyCore core) : base(core) { }

    public override void UseSharedInterface(IInterfaceManager interfaceManager)
    {
        shopApi = null;
        if (!interfaceManager.HasSharedInterface(ShopCoreInterfaceKey)) return;

        try
        {
            shopApi = interfaceManager.GetSharedInterface<IShopCoreApiV2>(ShopCoreInterfaceKey);
        }
        catch (Exception ex)
        {
            Core.Logger.LogError(ex, "Failed to resolve shared interface '{InterfaceKey}'.", ShopCoreInterfaceKey);
        }
    }

    public override void OnSharedInterfaceInjected(IInterfaceManager interfaceManager)
    {
        if (shopApi == null) return;
        RegisterItemsAndHandlers();
    }

    public override void Load(bool hotReload)
    {
        Core.Event.OnClientDisconnected += OnClientDisconnected;
        beamSweepTimer = Core.Scheduler.DelayAndRepeatBySeconds(
            BeamSweepIntervalSeconds,
            BeamSweepIntervalSeconds,
            () => Core.Scheduler.NextWorldUpdate(SweepExpiredBeams)
        );

        if (shopApi is not null && !handlersRegistered)
        {
            RegisterItemsAndHandlers();
        }
    }

    public override void Unload()
    {
        Core.Event.OnClientDisconnected -= OnClientDisconnected;
        previewRuntimeByPlayerId.Clear();
        activeRuntimeByPlayerId.Clear();
        nextDrawAllowedAtByPlayerId.Clear();

        if (beamSweepTimer is not null)
        {
            beamSweepTimer.Cancel();
            beamSweepTimer.Dispose();
            beamSweepTimer = null;
        }

        DespawnAllBeams();
        UnregisterItemsAndHandlers();
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent e)
    {
        previewRuntimeByPlayerId.Remove(e.PlayerId);
        activeRuntimeByPlayerId.Remove(e.PlayerId);
        nextDrawAllowedAtByPlayerId.Remove(e.PlayerId);
        DespawnPlayerPool(e.PlayerId);
    }

    [GameEventHandler(HookMode.Pre)]
    public HookResult OnBulletImpact(EventBulletImpact e)
    {
        if (shopApi == null || !handlersRegistered) return HookResult.Continue;

        var player = e.UserIdPlayer;
        if (player == null || !player.IsValid || player.IsFakeClient) return HookResult.Continue;

        if (!TryGetActiveRuntime(player, out var runtime)) return HookResult.Continue;

        var now = Core.Engine.GlobalVars.CurrentTime;
        if (nextDrawAllowedAtByPlayerId.TryGetValue(player.PlayerID, out var nextAllowedAt) && now < nextAllowedAt)
        {
            return HookResult.Continue;
        }

        if (!TryGetTracerStart(player, runtime.OriginZOffset, out var start)) return HookResult.Continue;

        nextDrawAllowedAtByPlayerId[player.PlayerID] = now + Math.Max(runtimeSettings.MinDrawIntervalSeconds, 0.01f);

        var end = new Vector(e.X, e.Y, e.Z);
        var color = ResolveTracerColor(player, runtime);

        var playerId = player.PlayerID;
        Core.Scheduler.NextWorldUpdate(() => DrawTracer(playerId, start, end, color, runtime));
        return HookResult.Continue;
    }

    private void RegisterItemsAndHandlers()
    {
        if (shopApi == null) return;

        UnregisterItemsAndHandlers();

        var moduleConfig = shopApi.LoadModuleConfig<TracersModuleConfig>(ModulePluginId, TemplateFileName, TemplateSectionName);
        NormalizeConfig(moduleConfig);

        runtimeSettings = moduleConfig.Settings;

        var category = string.IsNullOrWhiteSpace(moduleConfig.Settings.Category) ? DefaultCategory : moduleConfig.Settings.Category.Trim();

        if (moduleConfig.Items.Count == 0)
        {
            moduleConfig = CreateDefaultConfig();
            category = moduleConfig.Settings.Category;
            runtimeSettings = moduleConfig.Settings;

            _ = shopApi.SaveModuleConfig(ModulePluginId, moduleConfig, TemplateFileName, TemplateSectionName, overwrite: true);
        }

        foreach (var itemTemplate in moduleConfig.Items)
        {
            if (!TryCreateDefinition(itemTemplate, moduleConfig.Settings, category, out var definition, out var runtime)) continue;

            if (!shopApi.RegisterItem(definition)) continue;

            _ = registeredItemIds.Add(definition.Id);
            registeredItemOrder.Add(definition.Id);
            itemRuntimeById[definition.Id] = runtime;
        }

        shopApi.OnBeforeItemPurchase += OnBeforeItemPurchase;
        shopApi.OnItemToggled += OnItemToggled;
        shopApi.OnItemSold += OnItemSold;
        shopApi.OnItemExpired += OnItemExpired;
        shopApi.OnItemPreview += OnItemPreview;
        handlersRegistered = true;
    }

    private void UnregisterItemsAndHandlers()
    {
        if (!handlersRegistered || shopApi == null) return;

        shopApi.OnBeforeItemPurchase -= OnBeforeItemPurchase;
        shopApi.OnItemToggled -= OnItemToggled;
        shopApi.OnItemSold -= OnItemSold;
        shopApi.OnItemExpired -= OnItemExpired;
        shopApi.OnItemPreview -= OnItemPreview;

        foreach (var itemId in registeredItemIds) { _ = shopApi.UnregisterItem(itemId); }

        registeredItemIds.Clear();
        registeredItemOrder.Clear();
        itemRuntimeById.Clear();
        handlersRegistered = false;
    }

    private void OnBeforeItemPurchase(ShopBeforePurchaseContext context)
    {
        if (!registeredItemIds.Contains(context.Item.Id) || !itemRuntimeById.TryGetValue(context.Item.Id, out var runtime)) return;

        if (string.IsNullOrWhiteSpace(runtime.RequiredPermission)) return;

        if (Core.Permission.PlayerHasPermission(context.Player.SteamID, runtime.RequiredPermission)) return;

        var player = context.Player;
        var loc = Core.Translation.GetPlayerLocalizer(player);
        context.Block($"{GetPrefix(player)} {loc["error.permission", shopApi?.GetItemDisplayName(player, context.Item) ?? context.Item.DisplayName, runtime.RequiredPermission]}");
    }

    private void OnItemToggled(IPlayer player, ShopItemDefinition item, bool enabled)
    {
        activeRuntimeByPlayerId.Remove(player.PlayerID);

        if (!enabled || shopApi == null || !registeredItemIds.Contains(item.Id))
        {
            ReclaimPoolIfNoActiveTracer(player);
            return;
        }

        foreach (var otherItemId in registeredItemOrder)
        {
            if (string.Equals(otherItemId, item.Id, StringComparison.OrdinalIgnoreCase)) continue;
            if (!shopApi.IsItemEnabled(player, otherItemId)) continue;

            _ = shopApi.SetItemEnabled(player, otherItemId, false);
        }
    }

    private void OnItemSold(IPlayer player, ShopItemDefinition item, decimal amount)
    {
        activeRuntimeByPlayerId.Remove(player.PlayerID);
        ReclaimPoolIfNoActiveTracer(player);
    }

    private void OnItemExpired(IPlayer player, ShopItemDefinition item)
    {
        activeRuntimeByPlayerId.Remove(player.PlayerID);
        ReclaimPoolIfNoActiveTracer(player);
    }

    private void ReclaimPoolIfNoActiveTracer(IPlayer player)
    {
        if (!beamPoolByPlayerId.ContainsKey(player.PlayerID)) return;
        if (previewRuntimeByPlayerId.ContainsKey(player.PlayerID)) return;
        if (TryGetEnabledRuntime(player, out _)) return;

        DespawnPlayerPool(player.PlayerID);
    }

    private void OnItemPreview(IPlayer player, ShopItemDefinition item)
    {
        if (!registeredItemIds.Contains(item.Id) || !itemRuntimeById.TryGetValue(item.Id, out var runtime)) return;

        previewRuntimeByPlayerId[player.PlayerID] = new TracerPreviewState(runtime, Core.Engine.GlobalVars.CurrentTime + PreviewDurationSeconds);

        Core.Scheduler.NextWorldUpdate(() =>
        {
            if (!player.IsValid || player.IsFakeClient) return;

            var loc = Core.Translation.GetPlayerLocalizer(player);
            player.SendChat($"{GetPrefix(player)} {loc["preview.started", shopApi?.GetItemDisplayName(player, item) ?? item.DisplayName, (int)PreviewDurationSeconds]}");
        });
    }

    private string GetPrefix(IPlayer player)
    {
        var loc = Core.Translation.GetPlayerLocalizer(player);
        if (runtimeSettings.UseCorePrefix)
        {
            var corePrefix = shopApi?.GetShopPrefix(player);
            if (!string.IsNullOrWhiteSpace(corePrefix)) return corePrefix;
        }

        return loc["shop.prefix"];
    }

    private bool TryGetActiveRuntime(IPlayer player, out TracerItemRuntime runtime)
    {
        runtime = default;

        if (previewRuntimeByPlayerId.TryGetValue(player.PlayerID, out var preview))
        {
            if (Core.Engine.GlobalVars.CurrentTime <= preview.ExpiresAt)
            {
                runtime = preview.Runtime;
                return true;
            }

            previewRuntimeByPlayerId.Remove(player.PlayerID);
        }

        return TryGetEnabledRuntime(player, out runtime);
    }

    private bool TryGetEnabledRuntime(IPlayer player, out TracerItemRuntime runtime)
    {
        runtime = default;

        if (shopApi == null) return false;

        var now = Core.Engine.GlobalVars.CurrentTime;
        if (activeRuntimeByPlayerId.TryGetValue(player.PlayerID, out var cached) && now < cached.NextRefreshAt)
        {
            runtime = cached.Runtime;
            return cached.HasRuntime;
        }

        var found = false;
        foreach (var itemId in registeredItemOrder)
        {
            if (!itemRuntimeById.TryGetValue(itemId, out var itemRuntime)) continue;

            if (!shopApi.IsItemEnabled(player, itemId)) continue;

            runtime = itemRuntime;
            found = true;
            break;
        }

        activeRuntimeByPlayerId[player.PlayerID] = new CachedTracerRuntime(runtime, found, now + ActiveRuntimeRefreshSeconds);

        return found;
    }

    private static bool TryGetTracerStart(IPlayer player, float zOffset, out Vector start)
    {
        start = Vector.Zero;

        var pawn = player.PlayerPawn;
        if (pawn == null || !pawn.IsValid) return false;

        var origin = pawn.AbsOrigin;
        if (origin == null) return false;

        start = new Vector(origin.Value.X, origin.Value.Y, origin.Value.Z + zOffset);
        return true;
    }

    private Color ResolveTracerColor(IPlayer player, TracerItemRuntime runtime)
    {
        return runtime.ColorMode switch
        {
            TracerColorMode.Random => NextRandomColor(),
            TracerColorMode.Team => ResolveTeamColor(player),
            _ => runtime.StaticColor
        };
    }

    private Color ResolveTeamColor(IPlayer player)
    {
        return player.Controller.TeamNum == (int)Team.CT ? TeamCtColor : TeamTColor;
    }

    private Color NextRandomColor()
    {
        lock (random)
        {
            return new Color((byte)random.Next(0, 256), (byte)random.Next(0, 256), (byte)random.Next(0, 256), (byte)255);
        }
    }

    private void DrawTracer(int playerId, Vector start, Vector end, Color color, TracerItemRuntime runtime)
    {
        try
        {
            var poolSize = Math.Max(runtimeSettings.PoolSizePerPlayer, 1);
            if (!beamPoolByPlayerId.TryGetValue(playerId, out var pool))
            {
                pool = new TracerBeamPool(poolSize);
                beamPoolByPlayerId[playerId] = pool;
            }

            var slotIndex = pool.NextIndex;
            pool.NextIndex = (pool.NextIndex + 1) % pool.Slots.Length;

            var beam = pool.Slots[slotIndex];
            if (beam == null || !beam.IsValid)
            {
                beam = Core.EntitySystem.CreateEntityByDesignerName<CBeam>("beam");
                if (beam == null || !beam.IsValid) return;

                beam.Teleport(start, QAngle.Zero, Vector.Zero);
                beam.DispatchSpawn();
                pool.Slots[slotIndex] = beam;
            }

            beam.Teleport(start, QAngle.Zero, Vector.Zero);
            
            beam.EndPos.X = end.X;
            beam.EndPos.Y = end.Y;
            beam.EndPos.Z = end.Z;
            beam.EndPosUpdated();

            beam.Render = color;
            beam.RenderUpdated();

            beam.Width = runtime.StartWidth;
            beam.WidthUpdated();

            beam.EndWidth = runtime.EndWidth;
            beam.EndWidthUpdated();

            beam.TurnedOff = false;
            beam.TurnedOffUpdated();

            pool.HideAt[slotIndex] = Core.Engine.GlobalVars.CurrentTime + runtime.LifeSeconds;
            pool.Hidden[slotIndex] = false;
        }
        catch (Exception ex)
        {
            Core.Logger.LogWarning(ex, "Failed to draw tracer beam.");
        }
    }

    private void SweepExpiredBeams()
    {
        if (beamPoolByPlayerId.Count == 0) return;

        var now = Core.Engine.GlobalVars.CurrentTime;

        foreach (var pool in beamPoolByPlayerId.Values)
        {
            for (var i = 0; i < pool.Slots.Length; i++)
            {
                if (pool.Hidden[i]) continue;

                var beam = pool.Slots[i];
                if (beam == null || !beam.IsValid || now < pool.HideAt[i]) continue;

                try
                {
                    beam.TurnedOff = true;
                    beam.TurnedOffUpdated();
                }
                catch (Exception ex)
                {
                    Core.Logger.LogWarning(ex, "Failed to hide tracer beam entity.");
                }

                pool.Hidden[i] = true;
            }
        }
    }

    private void DespawnPlayerPool(int playerId)
    {
        if (!beamPoolByPlayerId.Remove(playerId, out var pool)) return;
        DespawnPool(pool);
    }

    private void DespawnAllBeams()
    {
        foreach (var pool in beamPoolByPlayerId.Values) DespawnPool(pool);
        beamPoolByPlayerId.Clear();
    }

    private void DespawnPool(TracerBeamPool pool)
    {
        foreach (var beam in pool.Slots)
        {
            if (beam == null) continue;
            DespawnBeam(beam);
        }
    }

    private void DespawnBeam(CBeam beam)
    {
        try
        {
            if (beam.IsValid) beam.Despawn();
        }
        catch (Exception ex)
        {
            Core.Logger.LogWarning(ex, "Failed to despawn tracer beam entity.");
        }
    }

    private bool TryCreateDefinition(
        TracerItemTemplate itemTemplate,
        TracersModuleSettings settings,
        string category,
        out ShopItemDefinition definition,
        out TracerItemRuntime runtime)
    {
        definition = default!;
        runtime = default;

        if (string.IsNullOrWhiteSpace(itemTemplate.Id)) return false;

        var itemId = itemTemplate.Id.Trim();

        if (itemTemplate.Price <= 0) return false;

        if (!Enum.TryParse(itemTemplate.Type, ignoreCase: true, out ShopItemType itemType) || itemType == ShopItemType.Consumable) return false;

        if (!Enum.TryParse(itemTemplate.Team, ignoreCase: true, out ShopItemTeam team)) team = ShopItemTeam.Any;

        TimeSpan? duration = null;
        if (itemTemplate.DurationSeconds > 0) duration = TimeSpan.FromSeconds(itemTemplate.DurationSeconds);

        if (itemType == ShopItemType.Temporary && !duration.HasValue) return false;

        decimal? sellPrice = null;
        if (itemTemplate.SellPrice.HasValue && itemTemplate.SellPrice.Value >= 0) sellPrice = itemTemplate.SellPrice.Value;

        if (!TryResolveColorMode(itemTemplate.Color, out var colorMode, out var staticColor)) return false;

        var lifeSeconds = itemTemplate.LifeSeconds ?? settings.DefaultLifeSeconds;
        if (lifeSeconds <= 0f) lifeSeconds = 0.4f;

        var startWidth = itemTemplate.StartWidth ?? settings.DefaultStartWidth;
        if (startWidth <= 0f) startWidth = 2.0f;

        var endWidth = itemTemplate.EndWidth ?? settings.DefaultEndWidth;
        if (endWidth <= 0f) endWidth = 1.0f;

        var originZOffset = itemTemplate.OriginZOffset ?? settings.DefaultOriginZOffset;

        definition = new ShopItemDefinition(
            Id: itemId,
            DisplayName: ResolveDisplayName(itemTemplate),
            Category: category,
            Price: itemTemplate.Price,
            SellPrice: sellPrice,
            Duration: duration,
            Type: itemType,
            Team: team,
            Enabled: itemTemplate.Enabled,
            CanBeSold: itemTemplate.CanBeSold,
            DisplayNameResolver: player => ResolveDisplayName(itemTemplate, player)
        );

        runtime = new TracerItemRuntime(
            ItemId: itemId,
            ColorMode: colorMode,
            StaticColor: staticColor,
            LifeSeconds: lifeSeconds,
            StartWidth: startWidth,
            EndWidth: endWidth,
            OriginZOffset: originZOffset,
            RequiredPermission: itemTemplate.RequiredPermission?.Trim() ?? string.Empty
        );

        return true;
    }

    private string ResolveDisplayName(TracerItemTemplate itemTemplate, IPlayer? player = null)
    {
        var colorName = string.IsNullOrWhiteSpace(itemTemplate.ColorDisplayName) ? itemTemplate.Color : itemTemplate.ColorDisplayName;

        if (!string.IsNullOrWhiteSpace(itemTemplate.DisplayNameKey))
        {
            var key = itemTemplate.DisplayNameKey.Trim();
            var localizer = player == null ? Core.Localizer : Core.Translation.GetPlayerLocalizer(player);
            var localized = itemTemplate.Type.Equals(nameof(ShopItemType.Permanent), StringComparison.OrdinalIgnoreCase)
                ? localizer[key, colorName]
                : localizer[key, colorName, FormatDuration(itemTemplate.DurationSeconds)];

            if (!string.Equals(localized, key, StringComparison.Ordinal)) return localized;
        }

        if (!string.IsNullOrWhiteSpace(itemTemplate.DisplayName)) return itemTemplate.DisplayName.Trim();

        return itemTemplate.Id.Trim();
    }

    private static bool TryResolveColorMode(string? value, out TracerColorMode mode, out Color color)
    {
        mode = TracerColorMode.Static;
        color = new Color((byte)255, (byte)255, (byte)255, (byte)255);

        if (string.IsNullOrWhiteSpace(value)) return false;

        var text = value.Trim();
        if (text.Equals("random", StringComparison.OrdinalIgnoreCase))
        {
            mode = TracerColorMode.Random;
            return true;
        }

        if (text.Equals("team", StringComparison.OrdinalIgnoreCase))
        {
            mode = TracerColorMode.Team;
            return true;
        }

        if (text.StartsWith('#'))
        {
            try
            {
                var sysColor = ColorTranslator.FromHtml(text);
                color = new Color(sysColor.R, sysColor.G, sysColor.B, sysColor.A);
                return true;
            }
            catch { return false; }
        }

        var builtin = SystemColor.FromName(text);
        if (!builtin.IsKnownColor && !builtin.IsNamedColor && !builtin.IsSystemColor) return false;

        color = new Color(builtin.R, builtin.G, builtin.B, builtin.A);
        return true;
    }

    private static string FormatDuration(int totalSeconds)
    {
        if (totalSeconds <= 0) return "0 Seconds";

        var ts = TimeSpan.FromSeconds(totalSeconds);
        if (ts.TotalHours >= 1)
        {
            var hours = (int)ts.TotalHours;
            var minutes = ts.Minutes;
            return minutes > 0 ? $"{hours} Hour{(hours == 1 ? "" : "s")} {minutes} Minute{(minutes == 1 ? "" : "s")}" : $"{hours} Hour{(hours == 1 ? "" : "s")}";
        }

        if (ts.TotalMinutes >= 1)
        {
            var minutes = (int)ts.TotalMinutes;
            var seconds = ts.Seconds;
            return seconds > 0 ? $"{minutes} Minute{(minutes == 1 ? "" : "s")} {seconds} Second{(seconds == 1 ? "" : "s")}" : $"{minutes} Minute{(minutes == 1 ? "" : "s")}";
        }

        return $"{ts.Seconds} Second{(ts.Seconds == 1 ? "" : "s")}";
    }

    private static void NormalizeConfig(TracersModuleConfig config)
    {
        config.Settings ??= new TracersModuleSettings();
        config.Items ??= [];

        config.Settings.Category = string.IsNullOrWhiteSpace(config.Settings.Category) ? DefaultCategory : config.Settings.Category.Trim();

        if (config.Settings.MinDrawIntervalSeconds <= 0f) config.Settings.MinDrawIntervalSeconds = 0.05f;
        if (config.Settings.PoolSizePerPlayer <= 0) config.Settings.PoolSizePerPlayer = 8;
        if (config.Settings.DefaultLifeSeconds <= 0f) config.Settings.DefaultLifeSeconds = 0.4f;
        if (config.Settings.DefaultStartWidth <= 0f) config.Settings.DefaultStartWidth = 2.0f;
        if (config.Settings.DefaultEndWidth <= 0f) config.Settings.DefaultEndWidth = 1.0f;
    }

    private static TracersModuleConfig CreateDefaultConfig()
    {
        return new TracersModuleConfig
        {
            Settings = new TracersModuleSettings
            {
                Category = DefaultCategory,
                DefaultLifeSeconds = 0.4f,
                DefaultStartWidth = 2.0f,
                DefaultEndWidth = 1.0f,
                DefaultOriginZOffset = 57f
            },
            Items =
            [
                new TracerItemTemplate
                {
                    Id = "red_tracer_hourly",
                    Color = "Red",
                    ColorDisplayName = "Red",
                    DisplayNameKey = "item.temporary.name",
                    Price = 1250,
                    SellPrice = 625,
                    DurationSeconds = 3600,
                    Type = nameof(ShopItemType.Temporary),
                    Team = nameof(ShopItemTeam.Any),
                    Enabled = true,
                    CanBeSold = true
                }
            ]
        };
    }
}
