using AeroCompliance.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AeroCompliance.Infrastructure.Persistence.Configurations;

public class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> b)
    {
        b.ToTable("roles");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
        b.Property(x => x.Name).HasColumnName("name").HasMaxLength(50).IsRequired();
        b.Property(x => x.Description).HasColumnName("description");
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");

        b.HasIndex(x => x.Name).IsUnique();

        // Seed the six canonical roles (matches RoleName enum + the Roles constants in the API).
        b.HasData(SeedData.Roles.Select(r => new Role
        {
            Id = r.Id,
            Name = r.Name,
            Description = r.Description,
            CreatedAt = SeedData.SeedTimestamp
        }));
    }
}

public class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    public void Configure(EntityTypeBuilder<Permission> b)
    {
        b.ToTable("permissions");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
        b.Property(x => x.Code).HasColumnName("code").HasMaxLength(100).IsRequired();
        b.Property(x => x.Description).HasColumnName("description");

        b.HasIndex(x => x.Code).IsUnique();
    }
}

public class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> b)
    {
        b.ToTable("role_permissions");
        b.HasKey(x => new { x.RoleId, x.PermissionId });
        b.Property(x => x.RoleId).HasColumnName("role_id");
        b.Property(x => x.PermissionId).HasColumnName("permission_id");

        b.HasOne(x => x.Role)
            .WithMany(r => r.RolePermissions)
            .HasForeignKey(x => x.RoleId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasOne(x => x.Permission)
            .WithMany(p => p.RolePermissions)
            .HasForeignKey(x => x.PermissionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class MunicipalityConfiguration : IEntityTypeConfiguration<Municipality>
{
    public void Configure(EntityTypeBuilder<Municipality> b)
    {
        b.ToTable("municipalities");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("uuid_generate_v4()");
        b.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        b.Property(x => x.Region).HasColumnName("region").HasMaxLength(100);
        b.Property(x => x.MouSignedAt).HasColumnName("mou_signed_at");
        b.Property(x => x.ApiAccessEnabled).HasColumnName("api_access_enabled").HasDefaultValue(false);
        b.Property(x => x.IsPilot).HasColumnName("is_pilot").HasDefaultValue(false);
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");

        // Seed a single pilot municipality so the dev test users have a valid FK target.
        b.HasData(new Municipality
        {
            Id = SeedData.PilotMunicipalityId,
            Name = SeedData.PilotMunicipalityName,
            Region = "Pilot",
            ApiAccessEnabled = false,
            IsPilot = true,
            CreatedAt = SeedData.SeedTimestamp
        });
    }
}

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.ToTable("users");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("uuid_generate_v4()");
        b.Property(x => x.FullName).HasColumnName("full_name").HasMaxLength(200).IsRequired();
        b.Property(x => x.Email).HasColumnName("email").HasMaxLength(200).IsRequired();
        b.Property(x => x.PasswordHash).HasColumnName("password_hash").IsRequired();
        b.Property(x => x.RoleId).HasColumnName("role_id").IsRequired();
        b.Property(x => x.MunicipalityId).HasColumnName("municipality_id");
        b.Property(x => x.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        b.Property(x => x.LastLoginAt).HasColumnName("last_login_at");

        b.HasIndex(x => x.Email).IsUnique();

        b.HasOne(x => x.Role)
            .WithMany(r => r.Users)
            .HasForeignKey(x => x.RoleId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(x => x.Municipality)
            .WithMany(m => m.Users)
            .HasForeignKey(x => x.MunicipalityId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> b)
    {
        b.ToTable("refresh_tokens");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("uuid_generate_v4()");
        b.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
        b.Property(x => x.FamilyId).HasColumnName("family_id").IsRequired();
        b.Property(x => x.TokenHash).HasColumnName("token_hash").IsRequired();
        b.Property(x => x.ExpiresAt).HasColumnName("expires_at").IsRequired();
        b.Property(x => x.RevokedAt).HasColumnName("revoked_at");
        b.Property(x => x.RotatedAt).HasColumnName("rotated_at");
        b.Property(x => x.ReuseDetectedAt).HasColumnName("reuse_detected_at");
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");

        b.Ignore(x => x.IsActive);
        b.HasIndex(x => x.FamilyId);

        b.HasOne(x => x.User)
            .WithMany(u => u.RefreshTokens)
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
