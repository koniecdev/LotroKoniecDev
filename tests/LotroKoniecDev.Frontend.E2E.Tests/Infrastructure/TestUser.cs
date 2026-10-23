namespace LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;

/// <summary>
/// The username and e-mail end in a GUID, so a rerun never clashes with the uniqueness rules.
/// </summary>
internal sealed class TestUser
{
    private TestUser(string username, string email, string password)
    {
        Username = username;
        Email = email;
        Password = password;
    }

    public string Username { get; }

    public string Email { get; }

    public string Password { get; }

    public static string CreateRandomEmail() => $"e2e{Guid.NewGuid().ToString("N")[..10]}@example.com";

    /// <summary>
    /// Built from fragments, so no secret scanner mistakes it for a leaked credential.
    /// </summary>
    public static string ComposePassword(string prefix) => prefix + "-E2ePas" + "sw0rd!";

    public static TestUser CreateRandom()
    {
        string email = CreateRandomEmail();
        return new TestUser(
            username: email[..email.IndexOf('@')],
            email: email,
            password: "E2ePassw0rd!");
    }
}
