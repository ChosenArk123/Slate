using System.Text;
using Slate.Core;

namespace Slate.Tests;

internal static class ImportAdversarialTests
{
    private static void Assert(bool value, string message = "Assertion failed.")
    {
        if (!value) throw new Exception(message);
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception($"Expected rejection with {typeof(T).Name}.");
    }

    public static void Run(Action<string, Action> check)
    {
        check("Adversarial: All 192 permutations of URL, Username, and Password aliases resolve correctly", () =>
        {
            var urlAliases = new[] { "url", "origin", "website", "web site", "login url", "page url", "action url", "host" };
            var userAliases = new[] { "username", "login", "user", "email", "account", "user name" };
            var passAliases = new[] { "password", "pass", "pwd", "secret" };

            var service = new BrowserDataImportService();
            int tested = 0;

            foreach (var u in urlAliases)
            {
                foreach (var usr in userAliases)
                {
                    foreach (var p in passAliases)
                    {
                        string headerLine = $"  {u.ToUpperInvariant()}  ,  {usr}  ,  {p.ToUpperInvariant()}  ";
                        string csv = $"{headerLine}\r\nhttps://example.test,testuser,testpass\r\n";
                        using var package = service.ParseCsv(new StringReader(csv));
                        Assert(package.Credentials.Count == 1, $"Failed for header: {headerLine}");
                        Assert(package.Credentials[0].Origin == "https://example.test");
                        Assert(package.Credentials[0].Username == "testuser");
                        Assert(package.Credentials[0].Password == "testpass");
                        tested++;
                    }
                }
            }
            Assert(tested == 192, $"Expected 192 combinations, tested {tested}");
        });

        check("Adversarial: Duplicate and conflicting column mappings are strictly rejected", () =>
        {
            var service = new BrowserDataImportService();
            var parser = new BrowserPasswordCsvParser();

            var duplicates = new[]
            {
                // URL duplicates
                "url,origin,username,password",
                "website,web site,username,password",
                "login url,page url,username,password",
                "action url,host,username,password",
                "url,url,username,password",
                "URL,url,username,password",

                // Username duplicates
                "url,username,login,password",
                "url,user,email,password",
                "url,account,user name,password",
                "url,username,username,password",
                "url,USERNAME,username,password",

                // Password duplicates
                "url,username,password,pass",
                "url,username,pwd,secret",
                "url,username,password,password",
                "url,username,PASSWORD,password",

                // Triplicate duplicates
                "url,origin,website,username,password",
                "url,username,login,user,password",
                "url,username,password,pass,pwd",

                // All duplicate
                "url,origin,username,login,password,secret"
            };

            foreach (var header in duplicates)
            {
                string csv = $"{header}\r\nhttps://example.test,alice,secret\r\n";
                Assert(!parser.CanImport(header.Split(',')), $"CanImport should be false for {header}");
                Throws<BrowserDataImportException>(() => service.ParseCsv(new StringReader(csv)));
            }
        });

        check("Adversarial: Invalid schemes, userinfo URLs, and malformed origins are strictly skipped/rejected", () =>
        {
            var service = new BrowserDataImportService();
            var hostileUrls = new[]
            {
                "file:///C:/Windows/System32/config/SAM",
                "file://localhost/etc/passwd",
                "ftp://ftp.example.test/credentials",
                "javascript:alert(document.cookie)",
                "data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==",
                "about:blank",
                "chrome://settings",
                "blob:https://example.test/f81d4fae-7dec-11d0-a765-00a0c91e6bf6",
                "gopher://example.test/",
                "ws://example.test/socket",
                "wss://example.test/socket",
                "https://admin:secretpassword@example.test/login",
                "http://user:pass@127.0.0.1:8080/",
                "https://user@example.test/login",
                @"https://example.test\evil.com/login",
                @"https://example.test:8080\path",
                "https://.../login",
                "https://example.test./login",
                "https://..example.test/login",
                "https:///missing-host",
                "not_a_url",
                "",
                "   ",
                "https://example test.com/login"
            };

            var sb = new StringBuilder();
            sb.AppendLine("url,username,password");
            foreach (var badUrl in hostileUrls)
            {
                sb.AppendLine($"\"{badUrl.Replace("\"", "\"\"")}\",hostile_user,hostile_pass");
            }
            sb.AppendLine("https://valid.example.test,good_user,good_pass");

            using var package = service.ParseCsv(new StringReader(sb.ToString()));
            Assert(package.Credentials.Count == 1, $"Expected exactly 1 valid credential, got {package.Credentials.Count}");
            Assert(package.Credentials[0].Origin == "https://valid.example.test");
            Assert(package.Credentials[0].Username == "good_user");
            Assert(package.RejectedRows == hostileUrls.Length, $"Expected {hostileUrls.Length} rejected rows, got {package.RejectedRows}");
        });

        check("Adversarial: Multiline quoted fields, escaped quotes, and empty fields parse accurately", () =>
        {
            var service = new BrowserDataImportService();
            string csv = "url,username,password,notes\r\n" +
                "https://example1.test,user1,\"pass\r\nwith\r\nnewlines\",\"line1\r\nline2\"\r\n" +
                "https://example2.test,user2,\"\"\"quoted_pass\"\"\",note2\r\n" +
                "https://example3.test,simple_user,\"p,a,s,s,w,o,r,d\",note3\r\n";

            using var package = service.ParseCsv(new StringReader(csv));
            Assert(package.Credentials.Count == 3);
            Assert(package.RejectedRows == 0);

            Assert(package.Credentials[0].Origin == "https://example1.test");
            Assert(package.Credentials[0].Username == "user1");
            Assert(package.Credentials[0].Password == "pass\r\nwith\r\nnewlines");

            Assert(package.Credentials[1].Origin == "https://example2.test");
            Assert(package.Credentials[1].Username == "user2");
            Assert(package.Credentials[1].Password == "\"quoted_pass\"");

            Assert(package.Credentials[2].Origin == "https://example3.test");
            Assert(package.Credentials[2].Username == "simple_user");
            Assert(package.Credentials[2].Password == "p,a,s,s,w,o,r,d");
        });

        check("Adversarial: Stray trailing characters after closing quote skips row without aborting next row", () =>
        {
            var service = new BrowserDataImportService();
            string csv = "url,username,password\r\n" +
                "\"https://bad.test\"trailing_chars,bad_user,bad_pass\r\n" +
                "https://good.test,good_user,good_pass\r\n";
            using var pkg = service.ParseCsv(new StringReader(csv));
            Assert(pkg.Credentials.Count == 1, $"Expected 1 valid credential, got {pkg.Credentials.Count}");
            Assert(pkg.Credentials[0].Origin == "https://good.test");
            Assert(pkg.RejectedRows == 1);
        });

        check("Adversarial: Oversized field marks row malformed without aborting next row", () =>
        {
            var service = new BrowserDataImportService();
            string hugeField = new string('A', 70_000);
            string csv = "url,username,password\r\n" +
                $"https://bad.test,{hugeField},bad_pass\r\n" +
                "https://good.test,good_user,good_pass\r\n";
            using var pkg = service.ParseCsv(new StringReader(csv));
            Assert(pkg.Credentials.Count == 1, $"Expected 1 valid credential, got {pkg.Credentials.Count}");
            Assert(pkg.Credentials[0].Origin == "https://good.test");
            Assert(pkg.RejectedRows == 1);
        });

        check("Adversarial: BOM marker handling across unquoted and quoted header rows", () =>
        {
            var service = new BrowserDataImportService();

            // Unquoted header with UTF-8 BOM
            string bomUnquoted = "\uFEFFurl,username,password\r\nhttps://example.test,alice,pass1\r\n";
            using var pkg1 = service.ParseCsv(new StringReader(bomUnquoted));
            Assert(pkg1.Credentials.Count == 1, "Unquoted header with UTF-8 BOM should parse 1 credential.");
            Assert(pkg1.Credentials[0].Origin == "https://example.test");

            // Quoted header with UTF-8 BOM
            string bomQuoted = "\uFEFF\"url\",\"username\",\"password\"\r\nhttps://example.test,bob,pass2\r\n";
            using var pkg2 = service.ParseCsv(new StringReader(bomQuoted));
            Assert(pkg2.Credentials.Count == 1, "Quoted header with UTF-8 BOM should parse 1 credential.");
            Assert(pkg2.Credentials[0].Origin == "https://example.test");

            // Mixed quoted and unquoted header with UTF-8 BOM and Unix newlines
            string bomMixed = "\uFEFF\"url\",username,\"password\"\nhttps://example.test,charlie,pass3\n";
            using var pkg3 = service.ParseCsv(new StringReader(bomMixed));
            Assert(pkg3.Credentials.Count == 1, "Mixed quoted header with UTF-8 BOM should parse 1 credential.");
            Assert(pkg3.Credentials[0].Username == "charlie");

            // Redundant BOM markers before quoted header
            string bomRedundant = "\uFEFF\uFEFF\"url\",\"username\",\"password\"\r\nhttps://example.test,dan,pass4\r\n";
            using var pkg4 = service.ParseCsv(new StringReader(bomRedundant));
            Assert(pkg4.Credentials.Count == 1, "Redundant BOM header should parse 1 credential.");
            Assert(pkg4.Credentials[0].Username == "dan");
        });

        check("Adversarial: Malformed rows with unescaped quotes do not swallow subsequent valid rows", () =>
        {
            var service = new BrowserDataImportService();

            string csv = "url,username,password\r\n" +
                "https://good1.test,user1,pass1\r\n" +
                "https://bad.test,bad\"user\",bad_pass\r\n" +
                "https://good2.test,user2,pass2\r\n" +
                "https://good3.test,user3,pass3\r\n";

            using var pkg = service.ParseCsv(new StringReader(csv));
            Assert(pkg.Credentials.Count == 3, $"Expected 3 valid credentials, but got {pkg.Credentials.Count}. Malformed row swallowed subsequent valid rows.");
            Assert(pkg.Credentials.Any(c => c.Origin == "https://good1.test"));
            Assert(pkg.Credentials.Any(c => c.Origin == "https://good2.test"));
            Assert(pkg.Credentials.Any(c => c.Origin == "https://good3.test"));

            // 4 unescaped quotes, 3 unescaped quotes, and trailing quote malformations followed by multiline quoted row
            string complexCsv = "url,username,password\r\n" +
                "https://bad1.test,two\"unescaped\"quotes\"here\",bad_pass1\r\n" +
                "https://good4.test,user4,pass4\r\n" +
                "https://bad2.test,odd\"count\"unescaped\",bad_pass2\r\n" +
                "https://bad3.test,\"bad\"trailing\"quotes,bad_pass3\r\n" +
                "https://good5.test,user5,\"pass\r\nwith\r\nnewlines\"\r\n" +
                "https://good6.test,user6,pass6\r\n";

            using var pkgComplex = service.ParseCsv(new StringReader(complexCsv));
            Assert(pkgComplex.Credentials.Count == 3, $"Expected 3 valid credentials from complex adversarial CSV, got {pkgComplex.Credentials.Count}.");
            Assert(pkgComplex.RejectedRows == 3, $"Expected 3 rejected rows, got {pkgComplex.RejectedRows}.");
            Assert(pkgComplex.Credentials[0].Origin == "https://good4.test");
            Assert(pkgComplex.Credentials[1].Origin == "https://good5.test" && pkgComplex.Credentials[1].Password == "pass\r\nwith\r\nnewlines");
            Assert(pkgComplex.Credentials[2].Origin == "https://good6.test");
        });
    }
}
