using FsCheck.Xunit;

namespace X402.Core.Tests;

public class FsCheckSmokeTests
{
    [Property]
    public bool Reversing_twice_is_identity(int[] xs) => xs.Reverse().Reverse().SequenceEqual(xs);
}
