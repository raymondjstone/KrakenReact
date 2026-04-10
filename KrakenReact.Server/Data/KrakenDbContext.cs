using KrakenReact.Server.Models;
using Kraken.Net.Objects.Models;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Data;

public class KrakenDbContext : DbContext
{
    public DbSet<EFAppCreds> AppCreds { get; set; }
    public DbSet<KrakenUserTrade> Trades { get; set; }
    public DbSet<KrakenLedgerEntry> Ledgers { get; set; }
    public DbSet<CombinedOrder> CombinedOrders { get; set; }
    public DbSet<KrakenSymbol> Symbols { get; set; }
    public DbSet<KrakenBalanceAvailable> Balances { get; set; }
    public DbSet<DerivedKline> DerivedKlines { get; set; }
    public DbSet<AppSettings> AppSettings { get; set; }
    public DbSet<AssetNormalization> AssetNormalizations { get; set; }

    public KrakenDbContext(DbContextOptions<KrakenDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EFAppCreds>(entity =>
        {
            entity.HasKey(e => e.id);
            entity.Property(e => e.appkey).IsRequired();
            entity.Property(e => e.appsecret).IsRequired();
        });
        modelBuilder.Ignore<KrakenOrderInfo>();
        modelBuilder.Ignore<KrakenFeeEntry>();
        modelBuilder.Entity<KrakenUserTrade>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Quantity).HasColumnType("decimal(38,9)");
            entity.Property(e => e.Price).HasColumnType("decimal(38,9)");
            entity.Property(e => e.QuoteQuantity).HasColumnType("decimal(38,9)");
            entity.Property(e => e.Fee).HasColumnType("decimal(38,9)");
            entity.Property(e => e.Margin).HasColumnType("decimal(38,9)");
            entity.Property(e => e.ClosedMargin).HasColumnType("decimal(38,9)");
            entity.Property(e => e.ClosedProfitLoss).HasColumnType("decimal(38,9)");
            entity.Property(e => e.ClosedAveragePrice).HasColumnType("decimal(38,9)");
            entity.Property(e => e.ClosedCost).HasColumnType("decimal(38,9)");
            entity.Property(e => e.ClosedFee).HasColumnType("decimal(38,9)");
            entity.Property(e => e.ClosedQuantity).HasColumnType("decimal(38,9)");
        });
        modelBuilder.Entity<KrakenLedgerEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Quantity).HasColumnType("decimal(38,9)");
            entity.Property(e => e.BalanceAfter).HasColumnType("decimal(38,9)");
            entity.Property(e => e.Fee).HasColumnType("decimal(38,9)");
        });
        modelBuilder.Entity<CombinedOrder>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Quantity).HasColumnType("decimal(38,9)");
            entity.Property(e => e.Price).HasColumnType("decimal(38,9)");
            entity.Property(e => e.OrderDetailsPrice).HasColumnType("decimal(38,9)");
            entity.Property(e => e.AveragePrice).HasColumnType("decimal(38,9)");
            entity.Property(e => e.SecondaryPrice).HasColumnType("decimal(38,9)");
            entity.Property(e => e.StopPrice).HasColumnType("decimal(38,9)");
            entity.Property(e => e.QuantityFilled).HasColumnType("decimal(38,9)");
        });
        modelBuilder.Entity<KrakenSymbol>(entity =>
        {
            entity.HasKey(e => e.WebsocketName);
            entity.Property(e => e.MinValue).HasColumnType("decimal(38,9)");
            entity.Property(e => e.OrderMin).HasColumnType("decimal(38,9)");
            entity.Property(e => e.TickSize).HasColumnType("decimal(38,9)");
        });
        modelBuilder.Entity<KrakenBalanceAvailable>(entity =>
        {
            entity.HasKey(e => e.Asset);
            entity.Property(e => e.Total).HasColumnType("decimal(38,9)");
            entity.Property(e => e.Locked).HasColumnType("decimal(38,9)");
        });
        modelBuilder.Entity<DerivedKline>(entity =>
        {
            entity.HasKey(e => e.Key);
            entity.HasIndex(e => e.Asset);
            entity.HasIndex(e => new { e.Asset, e.OpenTime });
            entity.Property(e => e.Open).HasColumnType("decimal(38,9)");
            entity.Property(e => e.High).HasColumnType("decimal(38,9)");
            entity.Property(e => e.Low).HasColumnType("decimal(38,9)");
            entity.Property(e => e.Close).HasColumnType("decimal(38,9)");
        });
        modelBuilder.Entity<AppSettings>(entity =>
        {
            entity.HasKey(e => e.Key);
        });
        modelBuilder.Entity<AssetNormalization>(entity =>
        {
            entity.HasKey(e => e.KrakenName);
        });
    }
}
