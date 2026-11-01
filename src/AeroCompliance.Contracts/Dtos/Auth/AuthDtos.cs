using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace AeroCompliance.Contracts.Dtos.Auth;

/// <summary>Credentials submitted to obtain an access + refresh token pair.</summary>
public sealed class LoginRequest
{
    public LoginRequest()
    {
    }

    [JsonConstructor]
    public LoginRequest(string email, string password)
    {
        Email = email;
        Password = password;
    }

    [Required, EmailAddress, StringLength(200)]
    public string Email { get; init; } = null!;

    [Required, StringLength(1024)]
    public string Password { get; init; } = null!;
}

/// <summary>Request to exchange a refresh token for a new access token (token rotation).</summary>
public sealed class RefreshTokenRequest
{
    public RefreshTokenRequest()
    {
    }

    [JsonConstructor]
    public RefreshTokenRequest(string refreshToken) => RefreshToken = refreshToken;

    [Required, StringLength(512)]
    public string RefreshToken { get; init; } = null!;
}

/// <summary>Request to revoke a refresh token.</summary>
public sealed class LogoutRequest
{
    public LogoutRequest()
    {
    }

    [JsonConstructor]
    public LogoutRequest(string refreshToken) => RefreshToken = refreshToken;

    [Required, StringLength(512)]
    public string RefreshToken { get; init; } = null!;
}

/// <summary>Token pair returned by login / refresh.</summary>
public record TokenResponse(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset AccessTokenExpiresAt);
