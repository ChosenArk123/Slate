using Slate.Core;

namespace Slate.Tests;

internal static class PasswordTests
{
    private static void Assert(bool value, string message = "Origin policy assertion failed.")
    {
        if (!value) throw new Exception(message);
    }

    public static void Run(Action<string, Action> check)
    {
        foreach (var (input, expected) in new (string, string)[] {
            ("https://Example.COM/a?q=1#b", "https://example.com"),
            ("http://EXAMPLE.com:80/", "http://example.com"),
            ("https://example.com:443/", "https://example.com"),
            ("https://example.com:8443/", "https://example.com:8443"),
            ("http://127.0.0.1:8080/a", "http://127.0.0.1:8080"),
            ("http://[::1]:80/a", "http://[::1]"),
            ("http://LOCALHOST:3000/a", "http://localhost:3000"),
            ("https://bücher.example/", "https://xn--bcher-kva.example"),
            ("https://xn--bcher-kva.example", "https://xn--bcher-kva.example")
        })
        {
            check("Credential origin canonicalization: " + input, () => Assert(CredentialOrigin.Normalize(input) == expected));
        }

        check("Credential origins isolate schemes, sibling subdomains and nondefault ports", () =>
        {
            var values = new[] { "http://example.com", "https://example.com", "https://example.com:8443", "https://a.example.com", "https://b.example.com" };
            Assert(values.Select(CredentialOrigin.Normalize).Distinct().Count() == values.Length);
        });

        check("Credential origins reject malformed, userinfo, nonweb and ambiguous inputs", () =>
        {
            foreach (var value in new string?[] {
                null, "", "example.com", "https:bad", "javascript:alert(1)", "file:///a",
                "https://a@b.test", "https://a.test:99999", "https://a.test./",
                "https://a.test\\@b.test", "https://a.test/\n"
            })
            {
                Assert(CredentialOrigin.Normalize(value) is null, $"Input '{value}' should be rejected");
            }
        });
    }
}
