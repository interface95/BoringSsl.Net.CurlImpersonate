namespace BoringSsl.Net.CurlImpersonate.UnitTests.CurlImpersonate;

using BoringSsl.Net.CurlImpersonate;

public sealed class CurlResponseHeaderParserTests
{
    [Fact]
    public void Parse_UsesLastStatusBlock_WhenMultipleStatusLinesExist()
    {
        var rawLines = new[]
        {
            "HTTP/1.1 100 Continue",
            string.Empty,
            "HTTP/2 200 OK",
            "content-type: application/json",
            "x-test: value",
            string.Empty,
        };

        var parsed = CurlResponseHeaderParser.Parse(rawLines);

        Assert.Equal(200, parsed.StatusCode);
        Assert.Equal("OK", parsed.ReasonPhrase);
        Assert.Equal(2, parsed.Headers.Count);
        Assert.Contains(parsed.Headers, static header => header.Name == "content-type" && header.Value == "application/json");
        Assert.Contains(parsed.Headers, static header => header.Name == "x-test" && header.Value == "value");
    }

    [Fact]
    public void Parse_IgnoresMalformedHeaderLines()
    {
        var rawLines = new[]
        {
            "HTTP/1.1 204 No Content",
            "x-valid: ok",
            "missing-colon",
            ":empty-name",
        };

        var parsed = CurlResponseHeaderParser.Parse(rawLines);

        Assert.Equal(204, parsed.StatusCode);
        Assert.Equal("No Content", parsed.ReasonPhrase);
        Assert.Single(parsed.Headers);
        Assert.Equal("x-valid", parsed.Headers[0].Name);
        Assert.Equal("ok", parsed.Headers[0].Value);
    }
}
