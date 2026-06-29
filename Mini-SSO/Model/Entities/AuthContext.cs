using Microsoft.EntityFrameworkCore;

namespace Mini_SSO.Model.Entities
{
    public class AuthContext(DbContextOptions<AuthContext> options) : DbContext(options)
    {
        public DbSet<Users> Users { get; set; }
        public DbSet<UserLogin> UserLogins { get; set; }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            builder.Entity<Users>(entity =>
            {
                entity.HasKey(e => e.UserId);

                entity.Property(e => e.UserId).HasDefaultValueSql("NEWID()");
                entity.Property(e => e.UserName).HasMaxLength(100).IsRequired();
                entity.Property(e => e.Email).HasMaxLength(200).IsRequired();
                entity.Property(e => e.PasswordHash).IsRequired(false);
                entity.Property(e => e.UpdateAt).HasDefaultValueSql("GETDATE()");
                entity.Property(e => e.CreateAt).HasDefaultValueSql("GETDATE()");

                entity.HasIndex(e => e.Email).IsUnique();
                entity.HasIndex(e => e.UserName).IsUnique();
            });

            builder.Entity<UserLogin>(entity =>
            {
                entity.HasKey(e => new { e.Provider, e.ProviderKey });

                entity.Property(e => e.Provider).IsRequired();
                entity.Property(e => e.UserId).IsRequired();

                entity
                    .HasOne(e => e.User)
                    .WithMany(u => u.UserLogins)
                    .HasForeignKey(e => e.UserId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasIndex(e => new { e.Provider, e.ProviderKey }).IsUnique();
            });
        }
    }
}
