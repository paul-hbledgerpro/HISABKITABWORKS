using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Core.Utils;
using Xunit;

namespace ManagerPaperworkSystem.Payroll.Tests;

public sealed class UserCredentialVerifierTests
{
    [Theory]
    [InlineData("1234", true)]
    [InlineData("0000", true)]
    [InlineData("123", false)]
    [InlineData("12345", false)]
    [InlineData("12a4", false)]
    [InlineData("", false)]
    public void PinValidationRequiresExactlyFourDigits(string pin, bool expected)
    {
        Assert.Equal(expected, UserCredentialVerifier.IsValidPin(pin));
    }

    [Fact]
    public void VerifiesExistingPasswordWhenNoPinIsConfigured()
    {
        var user = CreateUser("correct-password", null);

        Assert.True(UserCredentialVerifier.Verify(user, "correct-password"));
        Assert.False(UserCredentialVerifier.Verify(user, "1111"));
    }

    [Fact]
    public void VerifiesConfiguredPinAsAlternativeCredential()
    {
        var user = CreateUser("correct-password", "4826");

        Assert.True(UserCredentialVerifier.Verify(user, "correct-password"));
        Assert.True(UserCredentialVerifier.Verify(user, "4826"));
        Assert.False(UserCredentialVerifier.Verify(user, "4827"));
    }

    [Fact]
    public void DoesNotTreatNonFourDigitCredentialAsPin()
    {
        var user = CreateUser("correct-password", "4826");

        Assert.False(UserCredentialVerifier.Verify(user, "04826"));
        Assert.False(UserCredentialVerifier.Verify(user, "abcd"));
    }

    private static UserAccount CreateUser(string password, string? pin)
    {
        var (passwordHash, passwordSalt) = PasswordHasher.HashPassword(password);
        var user = new UserAccount
        {
            PasswordHashBase64 = passwordHash,
            SaltBase64 = passwordSalt
        };

        if (pin is not null)
        {
            var (pinHash, pinSalt) = PasswordHasher.HashPassword(pin);
            user.PinHashBase64 = pinHash;
            user.PinSaltBase64 = pinSalt;
        }

        return user;
    }
}
