using AeroCompliance.ApiService.Services;
using AeroCompliance.Domain.Entities;
using Microsoft.AspNetCore.Identity;

namespace AeroCompliance.UnitTests.Authentication;

public class PasswordHasherServiceTests
{
    [Fact]
    public void Dummy_verification_accepts_arbitrary_input_without_throwing()
    {
        var service = new PasswordHasherService(new PasswordHasher<User>());

        service.VerifyDummy("an attacker-controlled password");
    }
}
