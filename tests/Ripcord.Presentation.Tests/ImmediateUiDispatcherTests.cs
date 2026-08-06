using Ripcord.Presentation.Threading;
using Xunit;

namespace Ripcord.Presentation.Tests;

public class ImmediateUiDispatcherTests
{
    [Fact]
    public void Post_RunsInline_SoACallerObservesItsOwnWrite()
    {
        // The property this exists for: a test (or a host with no separate UI thread) can call a view-model
        // method and read the result on the very next line, with no draining step in between.
        var dispatcher = new ImmediateUiDispatcher();
        int value = 0;

        dispatcher.Post(() => value = 42);

        Assert.Equal(42, value);
    }

    [Fact]
    public void IsOnUiThread_IsAlwaysTrue()
        => Assert.True(new ImmediateUiDispatcher().IsOnUiThread);

    [Fact]
    public void Post_Null_Throws()
        => Assert.Throws<ArgumentNullException>(() => new ImmediateUiDispatcher().Post(null!));
}
