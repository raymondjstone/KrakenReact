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
    public DbSet<PredictionResult> PredictionResults { get; set; }
    public DbSet<PortfolioSnapshot> PortfolioSnapshots { get; set; }
    public DbSet<AlertLog> AlertLogs { get; set; }
    public DbSet<PriceAlert> PriceAlerts { get; set; }
    public DbSet<PredictionHistory> PredictionHistories { get; set; }
    public DbSet<DcaRule> DcaRules { get; set; }
    public DbSet<ProfitLadderRule> ProfitLadderRules { get; set; }
    public DbSet<MultiTfPredictionResult> MultiTfPredictionResults { get; set; }

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
        modelBuilder.Entity<PredictionResult>(entity =>
        {
            entity.HasKey(e => e.Symbol);
            entity.Property(e => e.ComputedAt)
                  .HasConversion(
                      v => v,
                      v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        });
        modelBuilder.Entity<PortfolioSnapshot>(entity =>
        {
            entity.HasKey(e => e.Date);
            entity.Property(e => e.TotalUsd).HasColumnType("decimal(38,2)");
            entity.Property(e => e.TotalGbp).HasColumnType("decimal(38,2)");
        });
        modelBuilder.Entity<AlertLog>(entity =>
        {
            entity.HasKey(e => e.Id);
        });
        modelBuilder.Entity<PriceAlert>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TargetPrice).HasColumnType("decimal(38,9)");
        });
        modelBuilder.Entity<PredictionHistory>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Symbol);
            entity.HasIndex(e => new { e.Symbol, e.ComputedAt });
        });
        modelBuilder.Entity<DcaRule>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.AmountUsd).HasColumnType("decimal(38,2)");
        });
        modelBuilder.Entity<ProfitLadderRule>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TriggerPct).HasColumnType("decimal(38,2)");
            entity.Property(e => e.SellPct).HasColumnType("decimal(38,2)");
        });
        modelBuilder.Entity<MultiTfPredictionResult>(entity =>
        {
            entity.HasKey(e => new { e.Symbol, e.Interval });
        });
    }
}
