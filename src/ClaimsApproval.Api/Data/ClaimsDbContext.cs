using ClaimsApproval.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace ClaimsApproval.Api.Data;

public class ClaimsDbContext(DbContextOptions<ClaimsDbContext> options) : DbContext(options)
{
    public DbSet<ExpenseClaim> Claims => Set<ExpenseClaim>();
    public DbSet<ClaimEvent> ClaimEvents => Set<ClaimEvent>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<ExpenseClaim>(e =>
        {
            e.ToTable("expense_claims");
            e.HasKey(c => c.Id);
            e.Property(c => c.EmployeeId).HasMaxLength(100).IsRequired();
            e.Property(c => c.Amount).HasPrecision(12, 2);
            e.Property(c => c.Currency).HasMaxLength(3).IsRequired();
            e.Property(c => c.Category).HasMaxLength(50).IsRequired();
            e.Property(c => c.Description).HasMaxLength(500).IsRequired();
            e.Property(c => c.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(c => c.DecisionReason).HasMaxLength(500);
            e.Property(c => c.Version).IsConcurrencyToken();
            e.HasIndex(c => c.Status);
            e.HasIndex(c => c.EmployeeId);

            e.HasMany(c => c.History).WithOne().HasForeignKey(h => h.ClaimId).OnDelete(DeleteBehavior.Cascade);
            e.Navigation(c => c.History).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        b.Entity<ClaimEvent>(e =>
        {
            e.ToTable("claim_events");
            e.HasKey(h => h.Id);
            e.Property(h => h.Action).HasMaxLength(50).IsRequired();
            e.Property(h => h.By).HasMaxLength(100).IsRequired();
            e.Property(h => h.Comment).HasMaxLength(500);
            e.HasIndex(h => h.ClaimId);
        });
    }
}
