using Microsoft.EntityFrameworkCore;
using OAuthLogin.Models;

namespace OAuthLogin.Data
{
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options)
        {
        }

        public DbSet<TokenRecord> UserTokens { get; set; }
        public DbSet<RawAnalyticsData> RawAnalyticsData { get; set; }
        public DbSet<NormalizedAnalyticsData> NormalizedAnalyticsData { get; set; }
        public DbSet<AnalyticsProcessingLog> AnalyticsProcessingLogs { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Configure TokenRecord entity
            modelBuilder.Entity<TokenRecord>(entity =>
            {
                entity.HasKey(e => e.Id);
                
                entity.Property(e => e.UserId)
                    .IsRequired()
                    .HasMaxLength(255);
                
                entity.Property(e => e.AccessToken)
                    .IsRequired()
                    .HasMaxLength(1000);
                
                entity.Property(e => e.RefreshToken)
                    .IsRequired()
                    .HasMaxLength(1000);
                
                entity.Property(e => e.TokenType)
                    .IsRequired()
                    .HasMaxLength(50);
                
                entity.Property(e => e.Scope)
                    .IsRequired()
                    .HasMaxLength(500);
                
                entity.Property(e => e.CreatedAt)
                    .HasDefaultValueSql("CURRENT_TIMESTAMP");
                
                entity.Property(e => e.UpdatedAt)
                    .HasDefaultValueSql("CURRENT_TIMESTAMP");
                
                entity.Property(e => e.IsActive)
                    .HasDefaultValue(true);

                // Create index on UserId for faster lookups
                entity.HasIndex(e => e.UserId);
            });

            // Configure RawAnalyticsData entity
            modelBuilder.Entity<RawAnalyticsData>(entity =>
            {
                entity.HasKey(e => e.Id);
                
                entity.Property(e => e.UserId)
                    .IsRequired()
                    .HasMaxLength(255);
                
                entity.Property(e => e.PropertyId)
                    .IsRequired()
                    .HasMaxLength(255);
                
                entity.Property(e => e.JsonData)
                    .IsRequired();
                
                entity.Property(e => e.Source)
                    .IsRequired()
                    .HasMaxLength(100);
                
                entity.Property(e => e.CreatedAt)
                    .HasDefaultValueSql("CURRENT_TIMESTAMP");

                // Create index on UserId and Timestamp for faster queries
                entity.HasIndex(e => e.UserId);
                entity.HasIndex(e => e.Timestamp);
            });

            // Configure NormalizedAnalyticsData entity
            modelBuilder.Entity<NormalizedAnalyticsData>(entity =>
            {
                entity.HasKey(e => e.Id);
                
                entity.Property(e => e.UserId)
                    .IsRequired()
                    .HasMaxLength(255);
                
                entity.Property(e => e.PropertyId)
                    .IsRequired()
                    .HasMaxLength(255);
                
                entity.Property(e => e.Country)
                    .IsRequired()
                    .HasMaxLength(100);
                
                entity.Property(e => e.ProcessingSource)
                    .IsRequired()
                    .HasMaxLength(100);
                
                entity.Property(e => e.ProcessedAt)
                    .HasDefaultValueSql("CURRENT_TIMESTAMP");

                // Create index on UserId and EventDate for faster queries
                entity.HasIndex(e => e.UserId);
                entity.HasIndex(e => e.EventDate);
            });

            // Configure AnalyticsProcessingLog entity
            modelBuilder.Entity<AnalyticsProcessingLog>(entity =>
            {
                entity.HasKey(e => e.Id);
                
                entity.Property(e => e.UserId)
                    .IsRequired()
                    .HasMaxLength(255);
                
                entity.Property(e => e.PropertyId)
                    .IsRequired()
                    .HasMaxLength(255);
                
                entity.Property(e => e.ProcessorType)
                    .IsRequired()
                    .HasMaxLength(100);
                
                entity.Property(e => e.ErrorMessage)
                    .HasMaxLength(1000);
                
                entity.Property(e => e.ProcessedAt)
                    .HasDefaultValueSql("CURRENT_TIMESTAMP");

                // Create index on ProcessedAt for faster queries
                entity.HasIndex(e => e.ProcessedAt);
            });
        }
    }
}