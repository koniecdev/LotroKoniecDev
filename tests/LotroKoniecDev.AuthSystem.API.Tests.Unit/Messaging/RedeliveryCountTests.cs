using LotroKoniecDev.AuthSystem.Infrastructure.Messaging;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Messaging;

public sealed class RedeliveryCountTests
{
    private const string DeliveryCountHeader = "x-delivery-count";

    [Fact]
    public void Read_NullHeaders_ReturnsZero()
    {
        int count = RedeliveryCount.Read(null);

        count.ShouldBe(0);
    }

    [Fact]
    public void Read_HeaderAbsent_ReturnsZero()
    {
        Dictionary<string, object?> headers = new()
        {
            ["some-other-header"] = 7L
        };

        int count = RedeliveryCount.Read(headers);

        count.ShouldBe(0);
    }

    [Theory]
    [InlineData(0L, 0)]
    [InlineData(3L, 3)]
    [InlineData(-1L, 0)]
    [InlineData(long.MaxValue, int.MaxValue)]
    public void Read_LongValue_ClampsIntoIntRange(long headerValue, int expected)
    {
        Dictionary<string, object?> headers = new()
        {
            [DeliveryCountHeader] = headerValue
        };

        int count = RedeliveryCount.Read(headers);

        count.ShouldBe(expected);
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(-5, 0)]
    public void Read_IntValue_ClampsNegativeToZero(int headerValue, int expected)
    {
        Dictionary<string, object?> headers = new()
        {
            [DeliveryCountHeader] = headerValue
        };

        int count = RedeliveryCount.Read(headers);

        count.ShouldBe(expected);
    }

    [Fact]
    public void Read_ByteValue_ReturnsValue()
    {
        Dictionary<string, object?> headers = new()
        {
            [DeliveryCountHeader] = (byte)4
        };

        int count = RedeliveryCount.Read(headers);

        count.ShouldBe(4);
    }

    [Theory]
    [InlineData("3")]
    [InlineData(null)]
    public void Read_UnreadableValue_ReturnsZero(object? headerValue)
    {
        Dictionary<string, object?> headers = new()
        {
            [DeliveryCountHeader] = headerValue
        };

        int count = RedeliveryCount.Read(headers);

        count.ShouldBe(0);
    }
}
