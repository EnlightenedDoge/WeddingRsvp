using System.Security.Cryptography;

namespace WeddingRsvp.Services;

public static class TokenGenerator
{
    public const int Length = 10;
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789"; // no 0/O/1/l/I

    public static string New()
    {
        Span<char> chars = stackalloc char[Length];
        for (var i = 0; i < Length; i++)
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }

    public const int GroupCodeLength = 6;

    /// <summary>The short code in a group link (/g/{code}). Same alphabet, never the length of a guest token.</summary>
    public static string NewGroupCode()
    {
        Span<char> chars = stackalloc char[GroupCodeLength];
        for (var i = 0; i < GroupCodeLength; i++)
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }

    public static bool LooksValidGroupCode(string? code) =>
        code is { Length: GroupCodeLength } && code.All(c => Alphabet.Contains(c));

    public static bool LooksValid(string? token) =>
        token is { Length: Length } && token.All(c => Alphabet.Contains(c));
}
