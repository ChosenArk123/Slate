using System.Text;
using Slate.Core;

namespace Slate.Tests;

internal static class PasswordCsvParsingTests
{
    private static void Assert(bool condition, string message = "Password CSV parser assertion failed.")
    {
        if (!condition) throw new Exception(message);
    }

    private static void Throws<T>(Action action, Action<T>? inspect = null) where T : Exception
    {
        try { action(); }
        catch (T exception) { inspect?.Invoke(exception); return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }

    public static void Run(Action<string, Action> check)
    {
        check("Password CSV parses strict UTF-8 BOM, reordered headers, unknown columns, quoting, newlines, and empty usernames", () =>
        {
            string csv = "\uFEFFpassword,unknown,website,username,name\r\n" +
                "\"p,ass\"\"word\",ignored,https://Example.test/login,álîce,Example\r\n" +
                "\"line1\r\nline2\",,https://newline.test/path,bob,Newline\r\n" +
                "empty-user,,https://empty-user.test/path,,Empty user\r\n" +
                ",,https://empty-password.test/path,nobody,Invalid\r\n";
            using var stream = new MemoryStream(new UTF8Encoding(false, true).GetBytes(csv));
            using var package = new BrowserDataImportService().ParseCsv(stream);

            Assert(package.Credentials.Count == 3 && package.RejectedRows == 1);
            Assert(package.Credentials[0].Origin == "https://example.test" && package.Credentials[0].Username == "álîce");
            Assert(package.Credentials[0].Password == "p,ass\"word");
            Assert(package.Credentials[1].Password == "line1\r\nline2");
            Assert(package.Credentials[2].Username == "" && package.Credentials[2].Password == "empty-user");
            Assert(package.SourceName == "Chrome or Edge password CSV" && stream.CanRead);
        });

        check("Password CSV rejects invalid UTF-8 without exposing row bytes", () =>
        {
            byte[] prefix = Encoding.ASCII.GetBytes("url,username,password\r\nhttps://example.test,alice,");
            byte[] bytes = [.. prefix, 0xFF, 0xFE, (byte)'\r', (byte)'\n'];
            using var stream = new MemoryStream(bytes);
            Throws<BrowserDataImportException>(() => new BrowserDataImportService().ParseCsv(stream), exception =>
            {
                Assert(exception.Message == "The selected CSV file is not valid UTF-8.");
                Assert(!exception.ToString().Contains("alice", StringComparison.Ordinal));
            });
        });

        check("Password CSV enforces the byte-size limit before parsing", () =>
        {
            using var stream = new MemoryStream();
            stream.SetLength(BrowserDataImportService.MaximumImportBytes + 1);
            Throws<BrowserDataImportException>(() => new BrowserDataImportService().ParseCsv(stream), exception =>
                Assert(exception.Message == "The CSV file exceeds the import size limit."));
        });

        check("Password CSV applies explicit URL, username, and password bounds with partial success", () =>
        {
            string maximumUrl = "https://bounded.test/" + new string('u',
                BrowserDataImportService.MaximumUrlCharacters - "https://bounded.test/".Length);
            string csv = "url,username,password\r\n" +
                new string('u', BrowserDataImportService.MaximumUrlCharacters + 1) + ",url-too-long,password\r\n" +
                "https://username-too-long.test," + new string('n', BrowserDataImportService.MaximumUsernameCharacters + 1) + ",password\r\n" +
                "https://password-too-long.test,user," + new string('p', BrowserDataImportService.MaximumPasswordCharacters + 1) + "\r\n" +
                maximumUrl + "," + new string('n', BrowserDataImportService.MaximumUsernameCharacters) + "," +
                new string('p', BrowserDataImportService.MaximumPasswordCharacters) + "\r\n";

            using var package = new BrowserDataImportService().ParseCsv(new StringReader(csv));
            Assert(package.Credentials.Count == 1 && package.RejectedRows == 3);
            Assert(package.Credentials[0].Origin == "https://bounded.test");
            Assert(package.Credentials[0].Username.Length == BrowserDataImportService.MaximumUsernameCharacters);
            Assert(package.Credentials[0].Password.Length == BrowserDataImportService.MaximumPasswordCharacters);
        });

        check("Password CSV converts malformed and unsupported rows to invalid records and continues", () =>
        {
            const string canary = "PASSWORD_CANARY_MUST_NOT_APPEAR";
            string csv = "origin,ignored,password,username\n" +
                "https://good-one.test,x,first,alice\n" +
                "https://bad-quote.test,x,bad\"quote,bob\n" +
                "https://too-few.test,x\n" +
                "ftp://unsupported.test,x," + canary + ",mallory\n" +
                "https://good-two.test,x,\"second,with comma\",carol\n";

            using var package = new BrowserDataImportService().ParseCsv(new StringReader(csv));
            Assert(package.Credentials.Count == 2 && package.RejectedRows == 3);
            Assert(package.Credentials.Select(record => record.Origin).SequenceEqual([
                "https://good-one.test", "https://good-two.test"]));
            Assert(package.Credentials[1].Password == "second,with comma");

            Throws<BrowserDataImportException>(() => new BrowserDataImportService().ParseCsv(
                new StringReader("unknown,columns\nvalue," + canary)), exception =>
            {
                Assert(!exception.Message.Contains(canary, StringComparison.Ordinal));
                Assert(!exception.ToString().Contains(canary, StringComparison.Ordinal));
            });
        });
    }
}
