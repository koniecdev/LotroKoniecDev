using System.Data.Common;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Npgsql;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Password;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Persistence;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

/// <summary>
/// A password change or reset saves the account once (#874). Identity gives the account a new security
/// stamp inside that one save. A second save only added one more write that could fail after the new
/// password was already in place: the caller got an error, and the other devices stayed signed in. So each
/// test arms a failure for a second save of the account, and it must never fire.
/// </summary>
public sealed partial class PasswordSaveTests : EndpointsTestBase
{
    private const string CurrentPassword = "TestPass1!";
    private const string NewPassword = "NewPass99!";

    public PasswordSaveTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    [Fact]
    public async Task ChangePassword_ShouldSaveTheAccountOnce()
    {
        // Arrange
        (RegisterRequest user, IdentityId userId) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, CurrentPassword);
        string accessToken = await GetAccessTokenAsync(user.Email, CurrentPassword);
        SaveCounter saves = FailASecondSaveOf(userId.Value);

        using HttpRequestMessage request = new(HttpMethod.Post, "auth/change-password");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(new ChangePasswordRequest(CurrentPassword, NewPassword));

        // Act
        using HttpResponseMessage response = await ApiClient.Http.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        saves.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ResetPassword_ShouldSaveTheAccountOnce()
    {
        // Arrange
        (RegisterRequest user, IdentityId userId) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, CurrentPassword);
        string resetToken = await RequestResetTokenAsync(user.Email);
        SaveCounter saves = FailASecondSaveOf(userId.Value);

        // Act
        using HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/reset-password", UriKind.Relative),
            new ResetPasswordRequest(user.Email, resetToken, NewPassword));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        saves.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ResetPasswordPage_Post_ShouldSaveTheAccountOnce()
    {
        // Arrange
        (RegisterRequest user, IdentityId userId) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, CurrentPassword);
        string resetToken = await RequestResetTokenAsync(user.Email);
        SaveCounter saves = FailASecondSaveOf(userId.Value);

        // Act
        using HttpResponseMessage response = await PostToResetPasswordPageAsync(new Dictionary<string, string>
        {
            ["Email"] = user.Email,
            ["Token"] = resetToken,
            ["NewPassword"] = NewPassword,
            ["ConfirmPassword"] = NewPassword
        });

        // Assert
        (await response.Content.ReadAsStringAsync()).ShouldContain("Hasło zmienione");
        saves.Count.ShouldBe(1);
    }

    /// <summary>
    /// Counts the saves of one account and fails the second one. Other accounts are left alone, so a
    /// background job that saves some other account cannot move the count.
    /// </summary>
    private SaveCounter FailASecondSaveOf(Guid userId)
    {
        SaveCounter saves = new();
        Factory.DbCommandFailures.FailNext(
            command => IsUpdateOfAccount(command, userId) && ++saves.Count > 1,
            () => new PostgresException(
                "simulated permanent failure", "ERROR", "ERROR", PostgresErrorCodes.NotNullViolation));

        return saves;
    }

    private static bool IsUpdateOfAccount(DbCommand command, Guid userId) =>
        command.CommandText.Contains($"UPDATE {DatabaseSchemas.Auth}.\"Users\"", StringComparison.Ordinal)
        && command.Parameters.Cast<DbParameter>().Any(parameter => parameter.Value is Guid id && id == userId);

    private async Task<string> RequestResetTokenAsync(string email)
    {
        PasswordResetEmailSpy.Reset();
        using HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/forgot-password", UriKind.Relative),
            new ForgotPasswordRequest(email));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        await PasswordResetEmailSpy.WaitForCaptureAsync();

        return PasswordResetEmailSpy.LastResetToken!;
    }

    private async Task<HttpResponseMessage> PostToResetPasswordPageAsync(Dictionary<string, string> formFields)
    {
        using HttpResponseMessage pageResponse = await ApiClient.Http.GetAsync(
            new Uri("/Account/ResetPassword", UriKind.Relative));
        pageResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        string html = await pageResponse.Content.ReadAsStringAsync();
        Match match = AntiForgeryTokenRegex().Match(html);
        if (match.Success)
        {
            formFields["__RequestVerificationToken"] = match.Groups[1].Value;
        }

        using FormUrlEncodedContent content = new(formFields);
        using HttpRequestMessage request = new(HttpMethod.Post, "/Account/ResetPassword") { Content = content };

        if (pageResponse.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies))
        {
            foreach (string cookie in cookies)
            {
                request.Headers.Add("Cookie", cookie.Split(';')[0]);
            }
        }

        return await ApiClient.Http.SendAsync(request);
    }

    [GeneratedRegex("""name="__RequestVerificationToken".*?value="([^"]+)""")]
    private static partial Regex AntiForgeryTokenRegex();

    private sealed class SaveCounter
    {
        public int Count { get; set; }
    }
}
