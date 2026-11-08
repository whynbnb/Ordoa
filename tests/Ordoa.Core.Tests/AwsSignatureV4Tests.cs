using System.Net.Http;
using Ordoa.Core.Util;

namespace Ordoa.Core.Tests;

public sealed class AwsSignatureV4Tests
{
    [Fact]
    public void Sign_MatchesIndependentReferenceVector()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://examplebucket.s3.amazonaws.com/test.txt");

        AwsSignatureV4.Sign(request, "us-east-1", "AKIAIOSFODNN7EXAMPLE",
            "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", null,
            AwsSignatureV4.EmptyPayloadHash,
            new DateTimeOffset(2013, 5, 24, 0, 0, 0, TimeSpan.Zero));

        Assert.NotNull(request.Headers.Authorization);
        Assert.Contains("Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request",
            request.Headers.Authorization!.Parameter);
        Assert.Contains("Signature=df548e2ce037944d03f3e68682813b093763996d597cf890ca3d9037fd231eb4",
            request.Headers.Authorization.Parameter);
    }

    [Fact]
    public void CanonicalQuery_SortsEncodedParameters()
    {
        var uri = new Uri("https://s3.example.com/bucket?prefix=a%20b&list-type=2&delimiter=%2F");
        var query = AwsSignatureV4.CanonicalQuery(uri);
        Assert.Equal("delimiter=%2F&list-type=2&prefix=a%20b", query);
    }

    [Fact]
    public void CanonicalPath_EncodesObjectKeys()
    {
        var uri = new Uri("https://s3.example.com/bucket/folder/a%20b.txt");
        Assert.Equal("/bucket/folder/a%20b.txt", AwsSignatureV4.CanonicalPath(uri));
    }
}
