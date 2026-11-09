using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ClaimsApproval.Api.Data;

/// <summary>Lets `dotnet ef migrations add` build the model without starting the whole app.</summary>
public class DesignTimeFactory : IDesignTimeDbContextFactory<ClaimsDbContext>
{
    public ClaimsDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ClaimsDbContext>()
            .UseNpgsql("Host=localhost;Database=claims;Username=claims;Password=claims")
            .Options);
}
