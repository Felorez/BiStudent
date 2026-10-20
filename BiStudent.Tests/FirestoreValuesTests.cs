using System.Text.Json;
using BiStudent.Server.Firebase;
using Xunit;

namespace BiStudent.Tests;

public sealed class FirestoreValuesTests
{
    private enum SampleKind { First, Second }

    public static TheoryData<object, Type> Values => new()
    {
        { true, typeof(bool) },
        { 42, typeof(int) },
        { 9876543210L, typeof(long) },
        { 1.25, typeof(double) },
        { "текст", typeof(string) },
        { new byte[] { 0, 1, 255 }, typeof(byte[]) },
        { new DateTimeOffset(2026, 9, 1, 12, 34, 56, TimeSpan.Zero), typeof(DateTimeOffset) },
        { new DateTime(2026, 9, 1, 12, 34, 56, 123), typeof(DateTime) },
        { new DateOnly(2026, 9, 1), typeof(DateOnly) },
        { new TimeOnly(12, 34, 56, 123), typeof(TimeOnly) },
        { SampleKind.Second, typeof(SampleKind) },
        { SampleKind.First, typeof(SampleKind?) },
        { new DateOnly(2026, 9, 1), typeof(DateOnly?) }
    };

    [Theory]
    [MemberData(nameof(Values))]
    public void ValueRoundTrips(object original, Type target)
    {
        using var document = JsonDocument.Parse(FirestoreValues.ToValue(original).ToJsonString());
        var actual = FirestoreValues.FromValue(document.RootElement, target);
        if (original is byte[] bytes)
            Assert.Equal(bytes, Assert.IsType<byte[]>(actual));
        else
            Assert.Equal(original, actual);
    }

    [Fact]
    public void NullRoundTrips()
    {
        using var document = JsonDocument.Parse(FirestoreValues.ToValue(null).ToJsonString());
        Assert.Null(FirestoreValues.FromValue(document.RootElement, typeof(DateOnly?)));
    }

    [Fact]
    public void IntegerValueMayBeString()
    {
        using var document = JsonDocument.Parse("""{"integerValue":"123456789"}""");
        Assert.Equal(123456789L,
            FirestoreValues.FromValue(document.RootElement, typeof(long)));
    }

    [Fact]
    public void UnknownEnumMemberFails()
    {
        using var document = JsonDocument.Parse("""{"stringValue":"Missing"}""");
        Assert.Throws<FormatException>(() =>
            FirestoreValues.FromValue(document.RootElement, typeof(SampleKind)));
    }
}
