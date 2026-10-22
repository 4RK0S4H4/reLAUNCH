using ReLaunch.Core;
using Xunit;

namespace ReLaunch.Core.Tests;

public class ProductInfoTests
{
    [Fact]
    public void DebugApiPortIsStable() => Assert.Equal(5480, ProductInfo.DebugApiPort);
}
