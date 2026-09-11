using System.Security.Cryptography;

namespace Slate.Core;

public static class PasswordGenerator
{
    public const int DefaultLength = 24;
    public const string Lower = "abcdefghijklmnopqrstuvwxyz";
    public const string Upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    public const string Digits = "0123456789";
    public const string Symbols = "!@#$%*-_+=?";
    public const string Alphabet = Lower + Upper + Digits + Symbols;

    public static string Generate(int length = DefaultLength)
    {
        if (length is < 16 or > 128) throw new ArgumentOutOfRangeException(nameof(length));
        var chars = new char[length];
        try
        {
            string[] classes = [Lower, Upper, Digits, Symbols];
            for (int i = 0; i < length; i++)
            {
                string alphabet = i < classes.Length ? classes[i] : Alphabet;
                chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
            }
            for (int i = length - 1; i > 0; i--)
            {
                int j = RandomNumberGenerator.GetInt32(i + 1);
                (chars[i], chars[j]) = (chars[j], chars[i]);
            }
            return new string(chars);
        }
        finally { Array.Clear(chars); }
    }
}
