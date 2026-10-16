using AuditTrail.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AuditTrail.Infrastructure.Persistence;

public partial class AppDbContext : DbContext
{
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        AuditingModelConfiguration.Configure(modelBuilder);
        modelBuilder.Entity<ImportCheckpoint>(entity =>
        {
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.Cursor).HasMaxLength(500);
        });
        modelBuilder.Entity<Category>(entity =>
        {
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.RowVersion).IsRowVersion();
            entity.Property(x => x.TenantId).HasMaxLength(100);
            entity.HasQueryFilter(x => x.TenantId == CurrentTenantId);
            entity.Property(x => x.Name).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(500);
        });

        modelBuilder.Entity<Supplier>(entity =>
        {
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.RowVersion).IsRowVersion();
            entity.Property(x => x.TenantId).HasMaxLength(100);
            entity.HasQueryFilter(x => x.TenantId == CurrentTenantId);
            entity.Property(x => x.Name).HasMaxLength(150).IsRequired();
            entity.Property(x => x.Email).HasMaxLength(254).IsRequired();
            entity.Property(x => x.InternalNote).HasMaxLength(500);
        });

        modelBuilder.Entity<Product>(entity =>
        {
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.RowVersion).IsRowVersion();
            entity.Property(x => x.TenantId).HasMaxLength(100);
            entity.HasQueryFilter(x => x.TenantId == CurrentTenantId);
            entity.Property(x => x.Name).HasMaxLength(150).IsRequired();
            entity.Property(x => x.Price).HasPrecision(18, 2);
            entity.HasOne(x => x.Category).WithMany(x => x.Products)
                .HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Supplier).WithMany(x => x.Products)
                .HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(table =>
            {
                table.HasCheckConstraint("CK_Products_Price", "[Price] >= 0");
                table.HasCheckConstraint("CK_Products_StockQuantity", "[StockQuantity] >= 0");
            });
        });
    }
}
