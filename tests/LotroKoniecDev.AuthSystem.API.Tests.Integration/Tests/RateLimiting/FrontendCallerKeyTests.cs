using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.Hateoas.Abstractions;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.RateLimiting;

/// <summary>
/// ADR-0054 (#819): every call the frontend makes reaches the auth API from its one container, so the
/// API meters it on the visitor's address the frontend forwards, but only next to the environment's
/// key. The limiter is forced on for a derived host, as the other rate-limiting suites do. In Testing
/// <c>UseForwardedHeaders</c> trusts every peer, so <c>X-Forwarded-For</c> plays the connection address
/// Caddy resolves: <c>10.60.0.x</c> is the frontend container, RFC 5737 addresses are visitors. Every
/// test creates its own host, so its buckets are its own. The full matrix of calls short of a proven
/// visitor is a unit test on the resolver; the rows here prove the wiring.
/// </summary>
public sealed class FrontendCallerKeyTests : EndpointsTestBase
{
    // Built rather than written out, so no secret scanner mistakes test data for a key.
    private static readonly string FrontendKey = new('k', 40);

    /// <summary>Mirrors the auth-endpoint-limit policy: 10 requests per minute per client.</summary>
    private const int SharedBucket = 10;

    /// <summary>Mirrors the change-email-limit policy: 3 sends per hour per client.</summary>
    private const int ChangeEmailBucket = 3;

    /// <summary>Mirrors the auth-page-limit policy: 10 POSTs per 15 minutes, per page and per address.</summary>
    private const int AuthPagePostBucket = 10;

    /// <summary>Mirrors the register-limit policy: 10 per minute per address.</summary>
    private const int RegisterBucket = 10;

    /// <summary>Mirrors forgot-password-limit and resend-confirmation-limit: 3 POSTs per 15 minutes per address.</summary>
    private const int MailBucket = 3;

    /// <summary>Mirrors the fixed-by-ip policy the discovery root sits on: 20 requests per minute per client.</summary>
    private const int GroupBucket = 20;

    private const string FrontendAddress = "10.60.0.7";
    private const string ForwardedForHeader = "X-Forwarded-For";
    private const string AccountPath = "auth/account/data-export";
    private const string ChangeEmailPath = "auth/account/change-email";
    private const string TokenPath = "connect/token";
    private const string LoginPagePath = "/Account/Login";
    private const string RegisterPath = "auth/register";
    private const string ForgotPasswordPath = "auth/forgot-password";
    private const string ResendConfirmationPath = "auth/resend-email-confirmation";
    private const string DiscoveryPath = "/";
    private const string Password = "TestPass1!";

    public FrontendCallerKeyTests(AuthSystemApiFactory appFactory) : base(appFactory)
    {
    }

    [Fact]
    public async Task TwoUsersBehindTheFrontend_ShouldEachGetTheirOwnTokenAndAccountBucket()
    {
        // Arrange: both users reach the auth API through the same frontend container. One of them
        // spends a whole bucket on a login and account page views, the way #819 describes.
        (RegisterRequest heavyUser, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, Password);
        (RegisterRequest bystander, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, Password);
        using WebApplicationFactory<Program> limitedHost = CreateRateLimitedHost();
        using HttpClient client = limitedHost.CreateClient();
        Caller heavyVisitor = Caller.ThroughTheFrontend("203.0.113.10");
        Caller bystanderVisitor = Caller.ThroughTheFrontend("203.0.113.11");

        string heavyToken = await GetAccessTokenAsync(client, heavyUser.Email, heavyVisitor);
        for (int i = 0; i < SharedBucket - 1; i++)
        {
            using HttpResponseMessage pageView = await GetAccountAsync(client, heavyToken, heavyVisitor);
            pageView.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using HttpResponseMessage bucketSpent = await GetAccountAsync(client, heavyToken, heavyVisitor);
        bucketSpent.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        // Act: the bystander logs in and opens their account page from the same container
        using HttpResponseMessage bystanderLogin = await PostTokenRequestAsync(client, bystander.Email, bystanderVisitor);
        string bystanderToken = bystanderLogin.IsSuccessStatusCode ? await ReadAccessTokenAsync(bystanderLogin) : string.Empty;
        using HttpResponseMessage bystanderPageView = await GetAccountAsync(client, bystanderToken, bystanderVisitor);

        // Assert: the bucket that filled is the first visitor's, not the container's
        bystanderLogin.StatusCode.ShouldBe(HttpStatusCode.OK);
        bystanderPageView.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ProvenVisitorCalls_ShouldNotSpendTheFrontendContainersOwnBucket()
    {
        // Arrange: a visitor spends a whole bucket through the frontend
        (RegisterRequest user, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, Password);
        using WebApplicationFactory<Program> limitedHost = CreateRateLimitedHost();
        using HttpClient client = limitedHost.CreateClient();
        Caller visitor = Caller.ThroughTheFrontend("203.0.113.20");

        string token = await GetAccessTokenAsync(client, user.Email, visitor);
        for (int i = 0; i < SharedBucket; i++)
        {
            using HttpResponseMessage pageView = await GetAccountAsync(client, token, visitor);
        }

        // Act: a call from the container itself, with neither header
        using HttpResponseMessage containersOwnCall = await PostTokenRequestAsync(client, user.Email, Caller.Direct(FrontendAddress));

        // Assert
        containersOwnCall.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AVisitor_ShouldHaveOneBucket_WhicheverWayTheCallArrives()
    {
        // Arrange: the visitor spends the bucket directly, without the frontend in between
        using WebApplicationFactory<Program> limitedHost = CreateRateLimitedHost();
        using HttpClient client = limitedHost.CreateClient();
        Caller direct = Caller.Direct("203.0.113.30");
        await ExhaustSharedBucketAsync(client, direct);

        // Act
        using HttpResponseMessage throughTheFrontend = await PostTokenRequestAsync(
            client, "burst@lotro-translator.pl", Caller.ThroughTheFrontend("203.0.113.30"));
        using HttpResponseMessage anotherVisitor = await PostTokenRequestAsync(
            client, "burst@lotro-translator.pl", Caller.ThroughTheFrontend("203.0.113.31"));

        // Assert: one visitor, one bucket; the container's key does not buy a second one
        throughTheFrontend.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        anotherVisitor.StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests);
    }

    public static TheoryData<string, string[], string[]> CallsShortOfAProvenVisitor => new()
    {
        { "10.60.0.31", [], ["198.51.100.31"] },
        { "10.60.0.32", [new string('w', 40)], ["198.51.100.32"] },
        { "10.60.0.33", [FrontendKey], [] },
        { "10.60.0.34", [FrontendKey], ["198.51.100.34", "203.0.113.9"] }
    };

    [Theory]
    [MemberData(nameof(CallsShortOfAProvenVisitor))]
    public async Task ACallShortOfAProvenVisitor_ShouldCountAgainstTheConnectionsOwnBucket(
        string connectionAddress,
        string[] keyValues,
        string[] addressValues)
    {
        // Arrange: the connection spends its own bucket with plain calls
        using WebApplicationFactory<Program> limitedHost = CreateRateLimitedHost();
        using HttpClient client = limitedHost.CreateClient();
        await ExhaustSharedBucketAsync(client, Caller.Direct(connectionAddress));

        // Act
        using HttpResponseMessage response = await PostTokenRequestAsync(
            client, "burst@lotro-translator.pl", new Caller(connectionAddress, keyValues, addressValues));

        // Assert: were the call believed, it would land in a fresh bucket instead of this full one
        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task TheEmailChangeMailBudget_ShouldBelongToTheVisitor_NotToTheFrontend()
    {
        // Arrange: the requests are unauthenticated on purpose, like ChangeEmailRateLimitingTests: the
        // limiter runs before authentication, so a 401 spends a permit like any other answer. One
        // visitor spends the whole mail budget through the frontend.
        using WebApplicationFactory<Program> limitedHost = CreateRateLimitedHost();
        using HttpClient client = limitedHost.CreateClient();
        Caller spender = Caller.ThroughTheFrontend("203.0.113.40");
        for (int i = 0; i < ChangeEmailBucket; i++)
        {
            using HttpResponseMessage send = await PostChangeEmailAsync(client, spender);
            send.StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests);
        }

        using HttpResponseMessage budgetSpent = await PostChangeEmailAsync(client, spender);
        budgetSpent.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        // Act: another visitor asks for an e-mail change through the same container
        using HttpResponseMessage otherVisitor = await PostChangeEmailAsync(client, Caller.ThroughTheFrontend("203.0.113.41"));

        // Assert
        otherVisitor.StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task TwoVisitorsBehindTheFrontend_ShouldEachGetTheirOwnDiscoveryBucket()
    {
        // Arrange: the discovery root is the one call the frontend makes on the group policy, on every
        // cold cache. One visitor spends the whole bucket.
        using WebApplicationFactory<Program> limitedHost = CreateRateLimitedHost();
        using HttpClient client = limitedHost.CreateClient();
        Caller heavyVisitor = Caller.ThroughTheFrontend("203.0.113.60");
        for (int i = 0; i < GroupBucket; i++)
        {
            using HttpResponseMessage fetch = await GetDiscoveryAsync(client, heavyVisitor);
            fetch.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using HttpResponseMessage bucketSpent = await GetDiscoveryAsync(client, heavyVisitor);
        bucketSpent.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        // Act
        using HttpResponseMessage otherVisitor = await GetDiscoveryAsync(client, Caller.ThroughTheFrontend("203.0.113.61"));

        // Assert
        otherVisitor.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    public static TheoryData<string, int, bool> KeyBlindEndpoints => new()
    {
        { LoginPagePath, AuthPagePostBucket, true },
        { RegisterPath, RegisterBucket, false },
        { ForgotPasswordPath, MailBucket, false },
        { ResendConfirmationPath, MailBucket, false }
    };

    [Theory]
    [MemberData(nameof(KeyBlindEndpoints))]
    public async Task AKeyBlindEndpoint_ShouldStayOnTheConnectionsOwnBucket_WhateverHeadersItCarries(
        string path,
        int budget,
        bool formBody)
    {
        // Arrange: the login form is the one place a password can be guessed in production, and the
        // three endpoints mail an address the caller typed. The frontend calls none of them, so their
        // policies ignore the key (ADR-0054 §3): a leaked key must not buy a fresh IP bucket per invented
        // address. Every POST here is refused before it does any
        // work (no antiforgery token, an empty body), and the limiter counts it all the same.
        using WebApplicationFactory<Program> limitedHost = CreateRateLimitedHost();
        using HttpClient client = limitedHost.CreateClient();
        Caller connection = Caller.Direct("203.0.113.50");
        for (int i = 0; i < budget; i++)
        {
            using HttpResponseMessage attempt = await PostRefusedAsync(client, path, formBody, connection);
            attempt.StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests);
        }

        // Act: the same connection, now claiming to be the frontend forwarding a fresh visitor
        using HttpResponseMessage claimedVisitor = await PostRefusedAsync(
            client, path, formBody, new Caller("203.0.113.50", [FrontendKey], ["198.51.100.50"]));

        // Assert
        claimedVisitor.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    /// <summary>
    /// Who a request comes from, as the auth API sees it: the connection address Caddy resolved, and
    /// the frontend's two headers when the call went through it.
    /// </summary>
    private sealed record Caller(string ConnectionAddress, IReadOnlyCollection<string> KeyValues, IReadOnlyCollection<string> AddressValues)
    {
        public static Caller Direct(string address) => new(address, [], []);

        public static Caller ThroughTheFrontend(string visitorAddress) => new(FrontendAddress, [FrontendKey], [visitorAddress]);
    }

    // Arrange-only: a login that is itself the Act of a test is sent with PostTokenRequestAsync and
    // asserted in the test body.
    private static async Task<string> GetAccessTokenAsync(HttpClient client, string email, Caller caller)
    {
        using HttpResponseMessage tokenResponse = await PostTokenRequestAsync(client, email, caller);
        tokenResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await ReadAccessTokenAsync(tokenResponse);
    }

    private static async Task<string> ReadAccessTokenAsync(HttpResponseMessage tokenResponse)
    {
        using JsonDocument json = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("access_token").GetString()!;
    }

    private static async Task<HttpResponseMessage> PostTokenRequestAsync(HttpClient client, string email, Caller caller)
    {
        // "username" is a fixed name in the OIDC protocol. What it carries is the e-mail (ADR-0022).
        using HttpRequestMessage request = CreateRequest(HttpMethod.Post, TokenPath, caller);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = email,
            ["password"] = Password,
            ["client_id"] = "lotrokoniecdev-test",
            ["scope"] = "email profile roles api"
        });

        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> GetAccountAsync(HttpClient client, string accessToken, Caller caller)
    {
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, AccountPath, caller);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> GetDiscoveryAsync(HttpClient client, Caller caller)
    {
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, DiscoveryPath, caller);
        return await client.SendAsync(request);
    }

    // A POST every endpoint refuses before it does any work: a form with no antiforgery token for a
    // page, an empty JSON object for an API endpoint.
    private static async Task<HttpResponseMessage> PostRefusedAsync(HttpClient client, string path, bool formBody, Caller caller)
    {
        using HttpRequestMessage request = CreateRequest(HttpMethod.Post, path, caller);
        request.Content = formBody
            ? new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Email"] = "burst@lotro-translator.pl",
                ["Password"] = "WrongPass1!"
            })
            : new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PostChangeEmailAsync(HttpClient client, Caller caller)
    {
        using HttpRequestMessage request = CreateRequest(HttpMethod.Post, ChangeEmailPath, caller);
        ChangeEmailRequest body = new("someone-else@lotro-translator.pl", Password);
        request.Content = JsonContent.Create(body, body.GetType());
        return await client.SendAsync(request);
    }

    // One Add per value, so two values arrive as a repeated header rather than one comma-joined value.
    private static HttpRequestMessage CreateRequest(HttpMethod method, string path, Caller caller)
    {
        HttpRequestMessage request = new(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(ForwardedForHeader, caller.ConnectionAddress);

        foreach (string keyValue in caller.KeyValues)
        {
            request.Headers.Add(FrontendCallerHeaders.Key, keyValue);
        }

        foreach (string addressValue in caller.AddressValues)
        {
            request.Headers.Add(FrontendCallerHeaders.ClientAddress, addressValue);
        }

        return request;
    }

    // Spends a whole shared bucket with token requests that fail client authentication: the limiter
    // runs before OpenIddict answers, so every one of them counts. Not an assertion: a bucket that did
    // not fill is a broken precondition for the test that called this, so it throws.
    private static async Task ExhaustSharedBucketAsync(HttpClient client, Caller caller)
    {
        for (int i = 0; i < SharedBucket; i++)
        {
            using HttpResponseMessage response = await PostTokenRequestAsync(client, "burst@lotro-translator.pl", caller);
        }

        using HttpResponseMessage probe = await PostTokenRequestAsync(client, "burst@lotro-translator.pl", caller);
        if (probe.StatusCode != HttpStatusCode.TooManyRequests)
        {
            throw new InvalidOperationException(
                $"The shared bucket did not fill after {SharedBucket} requests from {caller.ConnectionAddress} (the probe answered {probe.StatusCode}).");
        }
    }

    private WebApplicationFactory<Program> CreateRateLimitedHost()
    {
        return Factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "RateLimiting:ForceEnable", "true" },
                    { "FrontendCaller:Key", FrontendKey }
                });
            });
        });
    }
}
