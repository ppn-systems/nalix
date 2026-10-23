// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Exceptions;
using Nalix.Codec.Serialization;
using Nalix.Environment.Memory;

namespace Nalix.Codec.Tests.Serialization;

public sealed class NullableValueFormatterTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(42)]
    public void NullableIntRoundTrips(int? value) => RoundTrip(value);

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    [InlineData(false)]
    public void NullableBoolRoundTrips(bool? value) => RoundTrip(value);

    [Fact]
    public void NullableTimeSpanRoundTripsNull()
    {
        TimeSpan? value = null;

        RoundTrip(value);
    }

    [Fact]
    public void NullableTimeSpanRoundTripsValue()
    {
        TimeSpan? value = TimeSpan.FromMilliseconds(12345);

        RoundTrip(value);
    }

    [Fact]
    public void NullableGuidRoundTripsNull()
    {
        Guid? value = null;

        RoundTrip(value);
    }

    [Fact]
    public void NullableGuidRoundTripsValue()
    {
        Guid? value = Guid.Parse("d3a2f52c-8f6b-4f4e-9b30-5d2b2d87a72d");

        RoundTrip(value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(NullableTestStatus.Active)]
    [InlineData(NullableTestStatus.Archived)]
    public void NullableEnumRoundTrips(NullableTestStatus? value) => RoundTrip(value);

    [Fact]
    public void DeserializeWhenNullablePresenceFlagIsInvalidThrowsSerializationFailureException()
    {
        byte[] corrupt = [2];
        IFormatter<int?> formatter = FormatterProvider.Get<int?>();

        DataReader reader = new(corrupt);
        int? direct = formatter.Deserialize(ref reader);
        Assert.Null(direct);
        Assert.True(reader.IsFailed);

        _ = Assert.ThrowsAny<SerializationFailureException>(() => LiteSerializer.Deserialize<int?>(corrupt, out _));
    }

    [Fact]
    public void FormatterProviderResolvesNullableValueTypesWithoutManualRegistration()
    {
        IFormatter<int?> intFormatter = FormatterProvider.Get<int?>();
        IFormatter<TimeSpan?> timeSpanFormatter = FormatterProvider.Get<TimeSpan?>();

        Assert.NotNull(intFormatter);
        Assert.NotNull(timeSpanFormatter);
    }

    private static void RoundTrip<T>(T? value) where T : struct
    {
        byte[] bytes = LiteSerializer.Serialize(value);
        T? result = LiteSerializer.Deserialize<T?>(bytes, out int bytesRead);

        Assert.Equal(bytes.Length, bytesRead);
        Assert.Equal(value, result);
    }

    private static int? DeserializeNullable(IFormatter<int?> formatter, byte[] bytes)
    {
        DataReader reader = new(bytes);
        return formatter.Deserialize(ref reader);
    }

    public enum NullableTestStatus : byte
    {
        None = 0,
        Active = 1,
        Archived = 2
    }
}
