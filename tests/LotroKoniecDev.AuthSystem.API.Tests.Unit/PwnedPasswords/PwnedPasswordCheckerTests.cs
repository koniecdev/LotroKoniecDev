using System.Net;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using LotroKoniecDev.AuthSystem.Infrastructure.PwnedPasswords;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.PwnedPasswords;

public sealed class PwnedPasswordCheckerTests : IDisposable
{
    /// <summary>
    /// The example from the Have I Been Pwned documentation: SHA-1("password") is
    /// 5BAA61E4C9B93F3F0682250B6CF8331B7EE68FD8.
    /// </summary>
    private const string Password = "password";
    private const string HashPrefix = "5BAA6";
    private const string HashSuffix = "1E4C9B93F3F0682250B6CF8331B7EE68FD8";
    private const string OtherSuffix = "0018A45C4D1DEF81644B54AB7F969B88D65";

    private static readonly Uri RangeApiBaseAddress = new("https://api.pwnedpasswords.com/");

    private readonly MemoryCache _verdictCache = new(new MemoryCacheOptions());

    public void Dispose() => _verdictCache.Dispose();

    [Fact]
    public void HashForRangeApi_ForTheDocumentedExample_ReturnsTheUppercaseSha1()
    {
        string hash = PwnedPasswordChecker.HashForRangeApi(Password);

        hash.ShouldBe(HashPrefix + HashSuffix);
    }

    [Fact]
    public async Task CheckAsync_WhenTheAnswerListsTheSuffixWithACount_ReturnsBreached()
    {
        // Arrange
        using StubRangeApiHandler rangeApi = StubRangeApiHandler.Answering(
            $"{OtherSuffix}:1\r\n{HashSuffix}:10434004\r\n00D4F6E8FA6EECAD2A3AA415EEC418D38EC:2");
        PwnedPasswordChecker checker = CreateChecker(rangeApi);

        // Act
        PwnedPasswordVerdict verdict = await checker.CheckAsync(Password, CancellationToken.None);

        // Assert
        verdict.ShouldBe(PwnedPasswordVerdict.Breached);
    }

    [Fact]
    public async Task CheckAsync_WhenTheAnswerDoesNotListTheSuffix_ReturnsNotFound()
    {
        // Arrange
        using StubRangeApiHandler rangeApi = StubRangeApiHandler.Answering(
            $"{OtherSuffix}:1\r\n00D4F6E8FA6EECAD2A3AA415EEC418D38EC:2");
        PwnedPasswordChecker checker = CreateChecker(rangeApi);

        // Act
        PwnedPasswordVerdict verdict = await checker.CheckAsync(Password, CancellationToken.None);

        // Assert
        verdict.ShouldBe(PwnedPasswordVerdict.NotFound);
    }

    [Fact]
    public async Task CheckAsync_WhenTheSuffixIsOnlyAPaddingLine_ReturnsNotFound()
    {
        // Arrange: Add-Padding lines always carry a count of 0 and stand for no real password
        using StubRangeApiHandler rangeApi = StubRangeApiHandler.Answering(
            $"{OtherSuffix}:1\r\n{HashSuffix}:0");
        PwnedPasswordChecker checker = CreateChecker(rangeApi);

        // Act
        PwnedPasswordVerdict verdict = await checker.CheckAsync(Password, CancellationToken.None);

        // Assert
        verdict.ShouldBe(PwnedPasswordVerdict.NotFound);
    }

    [Fact]
    public async Task CheckAsync_WhenTheAnswerWritesTheSuffixInLowercase_ReturnsBreached()
    {
        // Arrange
        using StubRangeApiHandler rangeApi = StubRangeApiHandler.Answering(
            $"{HashSuffix.ToLowerInvariant()}:3");
        PwnedPasswordChecker checker = CreateChecker(rangeApi);

        // Act
        PwnedPasswordVerdict verdict = await checker.CheckAsync(Password, CancellationToken.None);

        // Assert
        verdict.ShouldBe(PwnedPasswordVerdict.Breached);
    }

    [Fact]
    public async Task CheckAsync_WhenTheAnswerNamesACharsetDotNetDoesNotKnow_StillReadsIt()
    {
        // Arrange: #1036 — decoding by the named charset would throw instead of answering
        using StubRangeApiHandler rangeApi = StubRangeApiHandler.Answering(
            $"{HashSuffix}:3", "text/plain; charset=x-no-such-charset");
        PwnedPasswordChecker checker = CreateChecker(rangeApi);

        // Act
        PwnedPasswordVerdict verdict = await checker.CheckAsync(Password, CancellationToken.None);

        // Assert
        verdict.ShouldBe(PwnedPasswordVerdict.Breached);
    }

    /// <summary>
    /// The acceptance criterion of #694: only the five-character prefix leaves the process. Neither the
    /// password nor the rest of its hash may appear anywhere in what is sent.
    /// </summary>
    [Fact]
    public async Task CheckAsync_ForAnyPassword_SendsOnlyTheFiveCharacterHashPrefixAndAsksForPadding()
    {
        // Arrange
        using StubRangeApiHandler rangeApi = StubRangeApiHandler.Answering(OtherSuffix + ":1");
        PwnedPasswordChecker checker = CreateChecker(rangeApi);

        // Act
        await checker.CheckAsync(Password, CancellationToken.None);

        // Assert
        StubRangeApiHandler.SentRequest sent = rangeApi.Requests.ShouldHaveSingleItem();
        sent.Method.ShouldBe(HttpMethod.Get);
        sent.Uri.ShouldBe(new Uri(RangeApiBaseAddress, "range/" + HashPrefix));
        sent.HasContent.ShouldBeFalse();
        sent.Headers.ShouldContain("Add-Padding: true");
        sent.Headers.ShouldNotContain(HashSuffix[..6], Case.Insensitive);
        sent.Headers.ShouldNotContain(Password, Case.Insensitive);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Found)]
    public async Task CheckAsync_WhenTheServiceAnswersWithAnythingButSuccess_ReturnsUnavailable(HttpStatusCode statusCode)
    {
        // Arrange
        using StubRangeApiHandler rangeApi = StubRangeApiHandler.AnsweringStatus(statusCode);
        PwnedPasswordChecker checker = CreateChecker(rangeApi);

        // Act
        PwnedPasswordVerdict verdict = await checker.CheckAsync(Password, CancellationToken.None);

        // Assert
        verdict.ShouldBe(PwnedPasswordVerdict.Unavailable);
    }

    [Fact]
    public async Task CheckAsync_WhenTheServiceCannotBeReached_ReturnsUnavailable()
    {
        // Arrange
        using StubRangeApiHandler rangeApi = StubRangeApiHandler.Throwing(
            new HttpRequestException("Name or service not known"));
        PwnedPasswordChecker checker = CreateChecker(rangeApi);

        // Act
        PwnedPasswordVerdict verdict = await checker.CheckAsync(Password, CancellationToken.None);

        // Assert
        verdict.ShouldBe(PwnedPasswordVerdict.Unavailable);
    }

    [Fact]
    public async Task CheckAsync_WhenTheServiceDoesNotAnswerInTime_ReturnsUnavailable()
    {
        // Arrange
        using StubRangeApiHandler rangeApi = StubRangeApiHandler.NeverAnswering();
        PwnedPasswordChecker checker = CreateChecker(rangeApi, TimeSpan.FromMilliseconds(50));

        // Act
        PwnedPasswordVerdict verdict = await checker.CheckAsync(Password, CancellationToken.None);

        // Assert
        verdict.ShouldBe(PwnedPasswordVerdict.Unavailable);
    }

    [Fact]
    public async Task CheckAsync_WhenTheCallerCancels_Throws()
    {
        // Arrange: a cancelled request is not a service outage, so it must not pass as one
        using StubRangeApiHandler rangeApi = StubRangeApiHandler.NeverAnswering();
        PwnedPasswordChecker checker = CreateChecker(rangeApi);
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        // Act & Assert
        await Should.ThrowAsync<OperationCanceledException>(() => checker.CheckAsync(Password, cancellation.Token));
    }

    [Theory]
    [InlineData(HashSuffix + ":5")]
    [InlineData(OtherSuffix + ":1")]
    public async Task CheckAsync_CalledTwiceWithTheSamePassword_AsksTheServiceOnce(string rangeBody)
    {
        // Arrange
        using StubRangeApiHandler rangeApi = StubRangeApiHandler.Answering(rangeBody);
        PwnedPasswordChecker checker = CreateChecker(rangeApi);

        // Act
        PwnedPasswordVerdict first = await checker.CheckAsync(Password, CancellationToken.None);
        PwnedPasswordVerdict second = await checker.CheckAsync(Password, CancellationToken.None);

        // Assert
        second.ShouldBe(first);
        rangeApi.Requests.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html><body>Access denied by your network administrator</body></html>")]
    [InlineData("<style>p { margin:0 }</style>")]
    public async Task CheckAsync_WhenASuccessfulAnswerHoldsNoHashLine_ReturnsUnavailable(string rangeBody)
    {
        // Arrange: a real answer always holds hundreds of lines, so this is a proxy or a broken answer
        using StubRangeApiHandler rangeApi = StubRangeApiHandler.Answering(rangeBody);
        PwnedPasswordChecker checker = CreateChecker(rangeApi);

        // Act
        PwnedPasswordVerdict verdict = await checker.CheckAsync(Password, CancellationToken.None);

        // Assert
        verdict.ShouldBe(PwnedPasswordVerdict.Unavailable);
    }

    [Fact]
    public async Task CheckAsync_AfterTheServiceWasUnavailable_AsksAgain()
    {
        // Arrange: an outage is not an answer, so it is never kept
        using StubRangeApiHandler rangeApi = StubRangeApiHandler.AnsweringStatus(HttpStatusCode.ServiceUnavailable);
        PwnedPasswordChecker checker = CreateChecker(rangeApi);

        // Act
        await checker.CheckAsync(Password, CancellationToken.None);
        await checker.CheckAsync(Password, CancellationToken.None);

        // Assert
        rangeApi.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task CheckAsync_ForTwoDifferentPasswords_AsksTheServiceForEach()
    {
        // Arrange
        using StubRangeApiHandler rangeApi = StubRangeApiHandler.Answering(OtherSuffix + ":1");
        PwnedPasswordChecker checker = CreateChecker(rangeApi);

        // Act
        await checker.CheckAsync(Password, CancellationToken.None);
        await checker.CheckAsync(Password + "1", CancellationToken.None);

        // Assert
        rangeApi.Requests.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("", PwnedPasswordVerdict.Unavailable)]
    [InlineData("\r\n\r\n", PwnedPasswordVerdict.Unavailable)]
    [InlineData(HashSuffix, PwnedPasswordVerdict.Unavailable)]
    [InlineData(HashSuffix + ":", PwnedPasswordVerdict.Unavailable)]
    [InlineData("<p style=\"margin:0\">Blocked</p>", PwnedPasswordVerdict.Unavailable)]
    [InlineData(OtherSuffix + ":1", PwnedPasswordVerdict.NotFound)]
    [InlineData(OtherSuffix + ":1\r\n" + HashSuffix + ":0", PwnedPasswordVerdict.NotFound)]
    [InlineData(OtherSuffix + ":1\r\n" + HashSuffix + ":abc", PwnedPasswordVerdict.NotFound)]
    [InlineData(OtherSuffix + ":1\r\n" + HashSuffix + ":-4", PwnedPasswordVerdict.NotFound)]
    [InlineData(OtherSuffix + ":1\r\n" + HashSuffix + "0:7", PwnedPasswordVerdict.NotFound)]
    [InlineData(OtherSuffix + ":1\r\n0" + HashSuffix + ":7", PwnedPasswordVerdict.NotFound)]
    [InlineData(HashSuffix + ":1", PwnedPasswordVerdict.Breached)]
    [InlineData(HashSuffix + ":1\r\n", PwnedPasswordVerdict.Breached)]
    [InlineData(HashSuffix + ": 12 ", PwnedPasswordVerdict.Breached)]
    [InlineData(HashSuffix + ":10434004", PwnedPasswordVerdict.Breached)]
    [InlineData(HashSuffix + ":99999999999", PwnedPasswordVerdict.Breached)]
    [InlineData("1e4c9b93f3f0682250b6cf8331b7ee68fd8:3", PwnedPasswordVerdict.Breached)]
    [InlineData("AAAA:1\n" + HashSuffix + ":2\n" + OtherSuffix + ":3", PwnedPasswordVerdict.Breached)]
    public void ReadRangeAnswer_ForEachAnswerShape_FindsOnlyARealBreachOfThisSuffix(
        string rangeBody,
        PwnedPasswordVerdict expected)
    {
        PwnedPasswordVerdict verdict = PwnedPasswordChecker.ReadRangeAnswer(rangeBody, HashSuffix);

        verdict.ShouldBe(expected);
    }

    private PwnedPasswordChecker CreateChecker(StubRangeApiHandler rangeApi, TimeSpan? timeout = null)
    {
        HttpClient httpClient = new(rangeApi, disposeHandler: false)
        {
            BaseAddress = RangeApiBaseAddress,
            Timeout = timeout ?? TimeSpan.FromSeconds(30)
        };

        return new PwnedPasswordChecker(httpClient, _verdictCache, NullLogger<PwnedPasswordChecker>.Instance);
    }
}
