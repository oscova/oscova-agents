using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Oscova.Agents.Core.Secrets;
using Oscova.Agents.Infrastructure.Auth;

namespace Oscova.Agents.Infrastructure.Data;

public sealed class ApplicationDbContext : IdentityDbContext<ApplicationUser>
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
        // Microsoft.Data.Sqlite 10 does not retry a busy autocommit of a single INSERT/UPDATE ... RETURNING, so a concurrent
        // reader fails it with "database is locked" at once; BEGIN IMMEDIATE and COMMIT are retried. Drop once the
        // provider ships the SqliteDataRecord busy-commit retry (dotnet/efcore main).
        Database.AutoTransactionBehavior = AutoTransactionBehavior.Always;
    }

    public DbSet<UserCredential> UserCredentials => Set<UserCredential>();
    public DbSet<SignInAttemptState> SignInAttempts => Set<SignInAttemptState>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<SignInAttemptState>(b =>
        {
            b.HasKey(x => new { x.UserId, x.Category });
            b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ApplicationUser>()
            .Property(x => x.VaultKeyEnvelope)
            .HasMaxLength(2048);

        builder.Entity<ApplicationUser>()
            .Property(x => x.DuressRecord)
            .HasMaxLength(32768);

        builder.Entity<UserCredential>(b =>
        {
            b.HasKey(x => x.Id);

            b.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(x => x.ProtectionUserId)
                .OnDelete(DeleteBehavior.Cascade);

            b.Property(x => x.UserId)
                .IsRequired()
                .HasMaxLength(450);

            b.Property(x => x.ProtectionUserId)
                .HasMaxLength(450);

            b.Property(x => x.CredentialKey)
                .IsRequired()
                .HasMaxLength(100);

            b.Property(x => x.Alias)
                .IsRequired()
                .HasMaxLength(200);

            b.Property(x => x.Description)
                .HasMaxLength(500);

            b.Property(x => x.Data)
                .IsRequired();

            b.Property(x => x.IsManaged)
                .IsRequired();

            b.HasIndex(x => new { x.UserId, x.CredentialKey })
                .IsUnique();
            b.HasIndex(x => x.OAuthProviderId).IsUnique().HasFilter("OAuthProviderId IS NOT NULL");
        });

    }
}
