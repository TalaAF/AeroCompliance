namespace AeroCompliance.Domain.Entities;

/// <summary>A hashed refresh token issued to a user. Maps to <c>refresh_tokens</c>.</summary>
public class RefreshToken
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>One login session and all of its refresh-token rotations.</summary>
    public Guid FamilyId { get; set; }

    public string TokenHash { get; set; } = null!;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset? RotatedAt { get; set; }
    public DateTimeOffset? ReuseDetectedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>True while the token has neither expired nor been revoked.</summary>
    public bool IsActive => RevokedAt is null && ExpiresAt > DateTimeOffset.UtcNow;
}
