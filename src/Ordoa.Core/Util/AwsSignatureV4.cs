using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Ordoa.Core.Util;

/// <summary>
/// AWS Signature Version 4 for S3-compatible services. The implementation is
/// intentionally small and signs the headers used by the S3 client only.
/// </summary>
public static class AwsSignatureV4
{
    public const string EmptyPayloadHash =
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    public static void Sign(HttpRequestMessage request, string region, string accessKey,
        string secretKey, string? sessionToken, string payloadHash, DateTimeOffset? now = null)
    {
        var timestamp = now ?? DateTimeOffset.UtcNow;
        var amzDate = timestamp.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var date = timestamp.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var uri = request.RequestUri ?? throw new InvalidOperationException("S3 请求缺少 URI");
        var host = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";

        request.Headers.Host = host;
        request.Headers.Remove("x-amz-date");
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.Remove("x-amz-content-sha256");
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        if (!string.IsNullOrEmpty(sessionToken))
        {
            request.Headers.Remove("x-amz-security-token");
            request.Headers.TryAddWithoutValidation("x-amz-security-token", sessionToken);
        }

        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = host,
            ["x-amz-content-sha256"] = payloadHash,
            ["x-amz-date"] = amzDate,
        };
        if (!string.IsNullOrEmpty(sessionToken))
            headers["x-amz-security-token"] = sessionToken;

        var signedHeaders = string.Join(';', headers.Keys);
        var canonicalHeaders = new StringBuilder();
        foreach (var pair in headers)
            canonicalHeaders.Append(pair.Key).Append(':').Append(NormalizeHeader(pair.Value)).Append('\n');

        var canonicalRequest = string.Join('\n',
            request.Method.Method,
            CanonicalPath(uri),
            CanonicalQuery(uri),
            canonicalHeaders.ToString(),
            signedHeaders,
            payloadHash);

        var scope = $"{date}/{region}/s3/aws4_request";
        var stringToSign = string.Join('\n',
            "AWS4-HMAC-SHA256",
            amzDate,
            scope,
            Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));

        var signingKey = GetSigningKey(secretKey, date, region);
        var signature = Hex(HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(stringToSign)));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "AWS4-HMAC-SHA256",
            $"Credential={accessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");
    }

    public static string CanonicalPath(Uri uri)
    {
        var path = uri.AbsolutePath;
        if (string.IsNullOrEmpty(path))
            return "/";

        var segments = path.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            if (segments[i].Length == 0)
                continue;
            segments[i] = Uri.EscapeDataString(Uri.UnescapeDataString(segments[i]));
        }
        return string.Join('/', segments);
    }

    public static string CanonicalQuery(Uri uri)
    {
        var query = uri.Query;
        if (string.IsNullOrEmpty(query) || query == "?")
            return string.Empty;

        var pairs = query[1..]
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair =>
            {
                var separator = pair.IndexOf('=');
                var key = separator < 0 ? pair : pair[..separator];
                var value = separator < 0 ? string.Empty : pair[(separator + 1)..];
                return (Key: EscapeQuery(key), Value: EscapeQuery(value));
            })
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ThenBy(pair => pair.Value, StringComparer.Ordinal);

        return string.Join('&', pairs.Select(pair => pair.Key + "=" + pair.Value));
    }

    private static string EscapeQuery(string value)
        => Uri.EscapeDataString(Uri.UnescapeDataString(value));

    private static string NormalizeHeader(string value)
    {
        var builder = new StringBuilder(value.Length);
        var lastWasSpace = false;
        foreach (var character in value.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                if (!lastWasSpace)
                    builder.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                builder.Append(character);
                lastWasSpace = false;
            }
        }
        return builder.ToString();
    }

    private static byte[] GetSigningKey(string secretKey, string date, string region)
    {
        var dateKey = HMACSHA256.HashData(Encoding.UTF8.GetBytes("AWS4" + secretKey),
            Encoding.UTF8.GetBytes(date));
        var regionKey = HMACSHA256.HashData(dateKey, Encoding.UTF8.GetBytes(region));
        var serviceKey = HMACSHA256.HashData(regionKey, Encoding.UTF8.GetBytes("s3"));
        return HMACSHA256.HashData(serviceKey, Encoding.UTF8.GetBytes("aws4_request"));
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
