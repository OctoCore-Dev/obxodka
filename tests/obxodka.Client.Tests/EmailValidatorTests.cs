using obxodka.Models;
using Xunit;

namespace obxodka.Client.Tests;

public sealed class EmailValidatorTests
{
    [Theory]
    [InlineData("user@gmail.com")]
    [InlineData("user@yandex.ru")]
    [InlineData("user@ya.ru")]
    [InlineData("user@mail.ru")]
    [InlineData("user@bk.ru")]
    [InlineData("user@inbox.ru")]
    [InlineData("user@list.ru")]
    [InlineData("user@outlook.com")]
    [InlineData("user@hotmail.com")]
    [InlineData("user@icloud.com")]
    [InlineData("user@proton.me")]
    [InlineData("google-reviewer@obxodka.one")]
    public void Validate_AllowedDomains_ReturnsValid(string email)
    {
        var result = EmailValidator.Validate(email);
        Assert.True(result.IsValid);
        Assert.Null(result.ErrorMessage);
        Assert.Null(result.SuggestedCorrection);
        Assert.Equal(email.ToLowerInvariant(), result.NormalizedEmail);
    }

    [Theory]
    [InlineData("irovbyte@b.ru", "irovbyte@bk.ru")]
    [InlineData("irovbytte@gamail.com", "irovbytte@gmail.com")]
    [InlineData("user@yadnex.ru", "user@yandex.ru")]
    [InlineData("user@yandx.ru", "user@yandex.ru")]
    [InlineData("user@mial.ru", "user@mail.ru")]
    [InlineData("user@mai.ru", "user@mail.ru")]
    [InlineData("user@gmail.ru", "user@gmail.com")]
    [InlineData("user@gmail.con", "user@gmail.com")]
    [InlineData("user@mail.rub", "user@mail.ru")]
    [InlineData("user@outlok.com", "user@outlook.com")]
    [InlineData("user@icoud.com", "user@icloud.com")]
    public void Validate_TypoDomains_ReturnsInvalidWithSuggestedCorrection(string email, string expectedSuggestion)
    {
        var result = EmailValidator.Validate(email);
        Assert.False(result.IsValid);
        Assert.NotNull(result.ErrorMessage);
        Assert.Equal(expectedSuggestion, result.SuggestedCorrection);
    }

    [Theory]
    [InlineData("олавыдоалды@yandex.ru")]
    [InlineData("юзер@gmail.com")]
    [InlineData("user@яндекс.рф")]
    [InlineData("testрус@mail.ru")]
    public void Validate_CyrillicLetters_ReturnsInvalid(string email)
    {
        var result = EmailValidator.Validate(email);
        Assert.False(result.IsValid);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("русские буквы", result.ErrorMessage);
    }

    [Theory]
    [InlineData("user@unknownrandomdomain123.com")]
    [InlineData("user@pupkin.xyz")]
    [InlineData("test@domain.org")]
    public void Validate_UnsupportedDomain_ReturnsInvalidWithoutSuggestion(string email)
    {
        var result = EmailValidator.Validate(email);
        Assert.False(result.IsValid);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("не поддерживается", result.ErrorMessage);
        Assert.Null(result.SuggestedCorrection);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("invalid-email")]
    [InlineData("user@")]
    [InlineData("@gmail.com")]
    [InlineData("user@@gmail.com")]
    [InlineData("user with spaces@gmail.com")]
    public void Validate_SyntaxErrors_ReturnsInvalid(string? email)
    {
        var result = EmailValidator.Validate(email);
        Assert.False(result.IsValid);
        Assert.NotNull(result.ErrorMessage);
    }
}
