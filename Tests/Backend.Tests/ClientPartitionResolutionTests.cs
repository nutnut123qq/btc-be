using System.Net;
using Microsoft.AspNetCore.Http;

namespace Backend.Tests;

public class ClientPartitionResolutionTests
{
    [Fact]
    public void DifferentForwardedClientIpsResolveToDifferentPartitions()
    {
        // Two real clients arriving through the same Funnel ingress IP must
        // not share a rate-limit partition.
        var first = CreateContext(remoteIp: "100.64.0.1", forwardedFor: "203.0.113.10");
        var second = CreateContext(remoteIp: "100.64.0.1", forwardedFor: "198.51.100.20");

        Assert.NotEqual(
            Program.ResolveClientPartition(first),
            Program.ResolveClientPartition(second));
    }

    [Fact]
    public void WithoutForwardedForFallsBackToRemoteIp()
    {
        var context = CreateContext(remoteIp: "100.64.0.7");

        Assert.Equal("100.64.0.7", Program.ResolveClientPartition(context));
    }

    [Fact]
    public void MalformedForwardedForFallsBackToRemoteIp()
    {
        var context = CreateContext(remoteIp: "100.64.0.7", forwardedFor: "not-an-ip");

        Assert.Equal("100.64.0.7", Program.ResolveClientPartition(context));
    }

    [Fact]
    public void MultiHopForwardedForUsesFirstHop()
    {
        var context = CreateContext(remoteIp: "100.64.0.7", forwardedFor: "203.0.113.10, 10.0.0.1, 10.0.0.2");

        Assert.Equal("203.0.113.10", Program.ResolveClientPartition(context));
    }

    [Fact]
    public void Ipv4MappedAndCompressedFormsShareTheIpv4Partition()
    {
        var mapped = CreateContext(remoteIp: "100.64.0.7", forwardedFor: "::ffff:203.0.113.10");
        var plain = CreateContext(remoteIp: "100.64.0.7", forwardedFor: "203.0.113.10");

        Assert.Equal("203.0.113.10", Program.ResolveClientPartition(mapped));
        Assert.Equal(Program.ResolveClientPartition(plain), Program.ResolveClientPartition(mapped));
    }

    [Fact]
    public void CompressedIpv6FormsNormalizeToOnePartition()
    {
        var expanded = CreateContext(remoteIp: "100.64.0.7", forwardedFor: "2001:0DB8:0000:0000:0000:0000:0000:0001");

        Assert.Equal("2001:db8::1", Program.ResolveClientPartition(expanded));
    }

    [Fact]
    public void NoIpInformationAtAllFallsBackToAnonymous()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = null;

        Assert.Equal("anonymous", Program.ResolveClientPartition(context));
    }

    [Fact]
    public void ConfiguredSecretRequiresIngressProofToTrustForwardedFor()
    {
        var proven = CreateContext("100.64.0.1", "203.0.113.10", ingressProof: "s3cret");
        Assert.Equal("203.0.113.10", Program.ResolveClientPartition(proven, "s3cret"));

        var missingProof = CreateContext("100.64.0.1", "203.0.113.10");
        Assert.Equal("100.64.0.1", Program.ResolveClientPartition(missingProof, "s3cret"));

        var wrongProof = CreateContext("100.64.0.1", "203.0.113.10", ingressProof: "wrong-secret");
        Assert.Equal("100.64.0.1", Program.ResolveClientPartition(wrongProof, "s3cret"));
    }

    [Fact]
    public void ConfiguredSecretDoesNotBlockRequestsWithoutForwardedFor()
    {
        var context = CreateContext("100.64.0.7");

        Assert.Equal("100.64.0.7", Program.ResolveClientPartition(context, "s3cret"));
    }

    private static DefaultHttpContext CreateContext(string remoteIp, string? forwardedFor = null, string? ingressProof = null)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        if (forwardedFor is not null)
        {
            context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        }
        if (ingressProof is not null)
        {
            context.Request.Headers[Program.IngressProofHeader] = ingressProof;
        }
        return context;
    }
}
