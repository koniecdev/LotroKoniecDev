namespace LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;

/// <summary>
/// A freshly-minted, globally-unique account for one registration flow. The username/email carry a
/// GUID suffix so reruns never collide on the Auth side's uniqueness rules, and the email is routed
/// to Mailpit (any domain works — nothing leaves the box). There is no phone number: unlike
/// TheKittySaver, our registration form has no phone field.
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

    public static TestUser CreateRandom()
    {
        string suffix = Guid.NewGuid().ToString("N")[..10];
        return new TestUser(
            username: $"e2e{suffix}",
            email: $"e2e{suffix}@example.com",
            password: "E2ePassw0rd!");
    }
}
