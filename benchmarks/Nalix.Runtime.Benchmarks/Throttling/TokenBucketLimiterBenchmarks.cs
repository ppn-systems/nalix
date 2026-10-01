using System;
using BenchmarkDotNet.Attributes;
using Nalix.Abstractions.Networking;
using Nalix.Environment.Configuration;
using Nalix.Runtime.Options;
using Nalix.Runtime.Throttling;
using Nalix.Benchmarks.Shared;

namespace Nalix.Runtime.Benchmarks.Throttling;

[Config(typeof(NalixBenchmarkConfig))]
public class TokenBucketLimiterBenchmarks
{
    private TokenBucketLimiter _limiter = null!;
    private INetworkEndpoint _endpoint = null!;

    private class BenchmarkNetworkEndpoint : INetworkEndpoint
    {
        public string Address => "127.0.0.1";
        public int Port => 80;
        public bool HasPort => true;
        public bool IsIPv6 => false;

        public override int GetHashCode() => Address.GetHashCode();
        public override bool Equals(object? obj) => obj is BenchmarkNetworkEndpoint other && Address == other.Address;
    }

    [GlobalSetup]
    public void Setup()
    {
        try { Nalix.Framework.Memory.Objects.ObjectPoolManager.Configure(new Nalix.Framework.Memory.Objects.ObjectPoolManager()); } catch (InvalidOperationException) { }

        var options = ConfigurationManager.Instance.Get<TokenBucketOptions>();
        options.CapacityTokens = 1000000;
        options.RefillTokensPerSecond = 1000000;
        options.ShardCount = 8;

        _limiter = new TokenBucketLimiter(options);
        _endpoint = new BenchmarkNetworkEndpoint();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _limiter.Dispose();
    }

    [Benchmark]
    public TokenBucketLimiter.RateLimitDecision Evaluate()
    {
        return _limiter.Evaluate(_endpoint);
    }
}
