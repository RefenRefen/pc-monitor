using Xunit;

namespace PcMonitor.Collector.Tests;

public class SmokeTests
{
    [Fact]
    public void Runs() => Assert.True(Environment.Is64BitProcess);
}
