using X402.Cryptography;

namespace X402.Core.Tests;

public sealed class Keccak256Tests
{
    [Theory]
    [InlineData("", "c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470")]
    [InlineData("abc", "4e03657aea45a94fc7d47ba826c8d667c0d1e6e33a64a036ec44f58fa12d6c45")]
    public void Matches_the_published_vectors(string input, string expected)
    {
        var hash = Keccak256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));

        Convert.ToHexString(hash).ToLowerInvariant().ShouldBe(expected);
    }
}
