namespace LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;

/// <summary>
/// A brand-new, unique account for one registration flow. The username and e-mail end in a GUID, so a
/// rerun never clashes with the auth server's uniqueness rules, and the e-mail goes to Mailpit, where
/// any domain works because nothing leaves the machine.
/// There is no phone number: unlike TheKittySaver, our registration form has no phone field.
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

    /// <summary>
    /// One address nobody has used yet, in the domain Mailpit accepts. A flow that only needs a free
    /// address calls this instead of building a whole second user.
    /// </summary>
    public static string CreateRandomEmail() => $"e2e{Guid.NewGuid().ToString("N")[..10]}@example.com";

    public static TestUser CreateRandom()
    {
        string email = CreateRandomEmail();
        return new TestUser(
            username: email[..email.IndexOf('@')],
            email: email,
            password: "E2ePassw0rd!");
    }
}
