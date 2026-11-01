using System.ComponentModel.DataAnnotations;
using AeroCompliance.Contracts.Dtos.Auth;
using AeroCompliance.Contracts.Dtos.Images;

namespace AeroCompliance.UnitTests.Authentication;

public class AuthDtoValidationTests
{
    [Fact]
    public void Login_rejects_missing_email()
        => AssertInvalid(new LoginRequest(null!, "ValidPassword123!"), nameof(LoginRequest.Email));

    [Fact]
    public void Login_rejects_invalid_email()
        => AssertInvalid(new LoginRequest("not-an-email", "ValidPassword123!"), nameof(LoginRequest.Email));

    [Fact]
    public void Login_rejects_missing_password()
        => AssertInvalid(new LoginRequest("person@example.test", "   "), nameof(LoginRequest.Password));

    [Fact]
    public void Refresh_rejects_empty_token()
        => AssertInvalid(new RefreshTokenRequest(" "), nameof(RefreshTokenRequest.RefreshToken));

    [Fact]
    public void Logout_rejects_empty_token()
        => AssertInvalid(new LogoutRequest(string.Empty), nameof(LogoutRequest.RefreshToken));

    [Fact]
    public void Login_rejects_oversized_credentials()
    {
        AssertInvalid(new LoginRequest($"{new string('a', 190)}@example.test", "ValidPassword123!"), nameof(LoginRequest.Email));
        AssertInvalid(new LoginRequest("person@example.test", new string('p', 1025)), nameof(LoginRequest.Password));
    }

    [Fact]
    public void Refresh_and_logout_reject_oversized_tokens()
    {
        var token = new string('t', 513);
        AssertInvalid(new RefreshTokenRequest(token), nameof(RefreshTokenRequest.RefreshToken));
        AssertInvalid(new LogoutRequest(token), nameof(LogoutRequest.RefreshToken));
    }

    [Fact]
    public void Image_upload_rejects_out_of_range_coordinates()
    {
        AssertInvalid(new ImageUploadForm { Latitude = 500 }, nameof(ImageUploadForm.Latitude));
        AssertInvalid(new ImageUploadForm { Longitude = -181 }, nameof(ImageUploadForm.Longitude));
    }

    private static void AssertInvalid(object value, string memberName)
    {
        var results = new List<ValidationResult>();
        var valid = Validator.TryValidateObject(value, new ValidationContext(value), results, true);

        Assert.False(valid);
        Assert.Contains(results, result => result.MemberNames.Contains(memberName));
    }
}
