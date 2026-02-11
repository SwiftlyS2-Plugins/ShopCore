using FreeSql;
using FreeSql.DataAnnotations;
using ShopCore.Contract;

namespace ShopCore;

internal sealed class FreeSqlShopLedgerStore : IShopLedgerStore
{
    private readonly IFreeSql orm;
    private readonly object sync = new();
    private readonly string mode;

    public FreeSqlShopLedgerStore(DataType dataType, string connectionString, bool autoSyncStructure)
    {
        orm = new FreeSqlBuilder()
            .UseConnectionString(dataType, connectionString)
            .UseAutoSyncStructure(autoSyncStructure)
            .Build();
        mode = $"FreeSql({dataType})";

        if (autoSyncStructure)
        {
            orm.CodeFirst.SyncStructure<ShopLedgerEntity>();
        }
    }

    public string Mode => mode;

    public void Record(ShopLedgerEntry entry)
    {
        var entity = new ShopLedgerEntity
        {
            TimestampUnixSeconds = entry.TimestampUnixSeconds,
            SteamId = unchecked((long)entry.SteamId),
            PlayerId = entry.PlayerId,
            PlayerName = entry.PlayerName,
            Action = entry.Action,
            Amount = entry.Amount,
            BalanceAfter = entry.BalanceAfter,
            ItemId = entry.ItemId,
            ItemDisplayName = entry.ItemDisplayName
        };

        lock (sync)
        {
            _ = orm.Insert(entity).ExecuteAffrows();
        }
    }

    public IReadOnlyCollection<ShopLedgerEntry> GetRecent(int maxEntries)
    {
        if (maxEntries <= 0)
        {
            return Array.Empty<ShopLedgerEntry>();
        }

        lock (sync)
        {
            var rows = orm.Select<ShopLedgerEntity>()
                .OrderByDescending(x => x.TimestampUnixSeconds)
                .OrderByDescending(x => x.Id)
                .Limit(maxEntries)
                .ToList();

            return rows.Select(ToRecord).ToArray();
        }
    }

    public IReadOnlyCollection<ShopLedgerEntry> GetRecentForSteamId(ulong steamId, int maxEntries)
    {
        if (steamId == 0 || maxEntries <= 0)
        {
            return Array.Empty<ShopLedgerEntry>();
        }

        var steamIdLong = unchecked((long)steamId);
        lock (sync)
        {
            var rows = orm.Select<ShopLedgerEntity>()
                .Where(x => x.SteamId == steamIdLong)
                .OrderByDescending(x => x.TimestampUnixSeconds)
                .OrderByDescending(x => x.Id)
                .Limit(maxEntries)
                .ToList();

            return rows.Select(ToRecord).ToArray();
        }
    }

    public void Dispose()
    {
        orm.Dispose();
    }

    private static ShopLedgerEntry ToRecord(ShopLedgerEntity entity)
    {
        return new ShopLedgerEntry(
            TimestampUnixSeconds: entity.TimestampUnixSeconds,
            SteamId: unchecked((ulong)entity.SteamId),
            PlayerId: entity.PlayerId,
            PlayerName: entity.PlayerName,
            Action: entity.Action,
            Amount: entity.Amount,
            BalanceAfter: entity.BalanceAfter,
            ItemId: entity.ItemId,
            ItemDisplayName: entity.ItemDisplayName
        );
    }

    [Table(Name = "shopcore_ledger")]
    private sealed class ShopLedgerEntity
    {
        [Column(IsPrimary = true, IsIdentity = true)]
        public long Id { get; set; }

        public long TimestampUnixSeconds { get; set; }
        public long SteamId { get; set; }
        public int PlayerId { get; set; }
        public string PlayerName { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public decimal BalanceAfter { get; set; }
        public string? ItemId { get; set; }
        public string? ItemDisplayName { get; set; }
    }
}
