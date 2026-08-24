using NetTrans.Services;
using Xunit;

namespace NetTrans.Tests;

/// <summary>全部完成后 has to be edge-triggered: an idle queue is the normal state.</summary>
public class QueueDrainTests
{
    [Fact]
    public void An_idle_queue_never_fires_on_its_own()
    {
        var drain = new QueueDrain();

        for (int tick = 0; tick < 10; tick++) Assert.False(drain.Drained(0));
    }

    [Fact]
    public void It_fires_once_when_the_last_transfer_stops()
    {
        var drain = new QueueDrain();

        Assert.False(drain.Drained(3));
        Assert.False(drain.Drained(1));
        Assert.True(drain.Drained(0));

        // And not again while the queue stays empty.
        Assert.False(drain.Drained(0));
        Assert.False(drain.Drained(0));
    }

    [Fact]
    public void A_second_batch_fires_again()
    {
        var drain = new QueueDrain();

        Assert.False(drain.Drained(2));
        Assert.True(drain.Drained(0));

        Assert.False(drain.Drained(1));
        Assert.True(drain.Drained(0));
    }

    [Fact]
    public void Cancelling_stands_down_this_batch_only()
    {
        var drain = new QueueDrain();

        drain.Drained(2);
        drain.Disarm();
        Assert.False(drain.Drained(0));

        // The next batch is a fresh question.
        Assert.False(drain.Drained(2));
        Assert.True(drain.Drained(0));
    }

    [Fact]
    public void It_reports_whether_anything_has_run_since_the_last_drain()
    {
        var drain = new QueueDrain();
        Assert.False(drain.IsArmed);

        drain.Drained(1);
        Assert.True(drain.IsArmed);

        drain.Drained(0);
        Assert.False(drain.IsArmed);
    }
}
