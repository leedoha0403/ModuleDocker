using System.IO;
using Dora.Widget.Host;

namespace Dora.Widget.Host.Tests;

public class SingleInstanceTests
{
    private static string UniqueDir() => Path.Combine(Path.GetTempPath(), "md-single-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Second_launch_is_refused_and_wakes_the_first()
    {
        var dir = UniqueDir();
        using var first = SingleInstance.Acquire(dir);
        Assert.NotNull(first);
        using var woke = new ManualResetEventSlim();
        first!.ActivationRequested += woke.Set;

        Assert.Null(SingleInstance.Acquire(dir));
        Assert.True(woke.Wait(TimeSpan.FromSeconds(3)), "the running Host was not asked to come forward");
    }

    [Fact]
    public void Different_data_folders_do_not_block_each_other_and_the_lock_is_released_on_dispose()
    {
        var a = UniqueDir();
        var b = UniqueDir();
        var first = SingleInstance.Acquire(a);
        using var other = SingleInstance.Acquire(b);
        Assert.NotNull(first);
        Assert.NotNull(other);

        first!.Dispose();
        using var again = SingleInstance.Acquire(a);
        Assert.NotNull(again);
    }

    [Fact]
    public void Names_are_stable_valid_and_case_and_slash_insensitive()
    {
        var n1 = SingleInstance.NameFor(@"C:\Data\ModuleDock");
        Assert.Equal(n1, SingleInstance.NameFor(@"c:\data\moduledock\"));
        Assert.StartsWith(@"Local\ModuleDock.", n1);
        Assert.NotEqual(n1, SingleInstance.NameFor(@"C:\Data\Other"));
    }
}
