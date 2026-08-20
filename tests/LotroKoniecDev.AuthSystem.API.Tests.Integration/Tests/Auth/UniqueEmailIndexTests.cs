using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

public sealed class UniqueEmailIndexTests : EndpointsTestBase
{
    public UniqueEmailIndexTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    [Fact]
    public async Task Users_ShouldRejectCaseVariantDuplicateEmail_AtTheDatabaseLevel()
    {
        // Arrange: first account through the normal path
        (RegisterRequest request, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

        string caseVariantEmail = request.Email.ToUpperInvariant();
        caseVariantEmail.ShouldNotBe(request.Email); // raw-column uniqueness must NOT be what trips below

        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        // A direct insert skips every check the app does, which is exactly the race RegisterUser
        // describes. Only the unique EmailIndex on NormalizedEmail stops this pair from locking two
        // accounts out of login for good (ADR-0022).
        string username = "dup" + Faker.Random.AlphaNumeric(12);
        ApplicationUser duplicate = new()
        {
            UserName = username,
            NormalizedUserName = username.ToUpperInvariant(),
            Email = caseVariantEmail,
            NormalizedEmail = request.Email.ToUpperInvariant(),
            SecurityStamp = Guid.NewGuid().ToString()
        };
        dbContext.Users.Add(duplicate);

        // Act + Assert
        await Should.ThrowAsync<DbUpdateException>(async () => await dbContext.SaveChangesAsync());
    }
}
