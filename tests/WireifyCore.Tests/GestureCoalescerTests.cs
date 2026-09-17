// SPDX-License-Identifier: Apache-2.0
using System;
using WireifyCore.Bridge;

namespace WireifyCore.Tests;

public class GestureCoalescerTests
{
    static readonly TimeSpan Gap = TimeSpan.FromMilliseconds(750);
    static readonly Guid Slider = Guid.NewGuid();

    [Fact]
    public void First_push_is_always_a_new_gesture()
    {
        var coalescer = new GestureCoalescer(Gap, clock: () => DateTime.UtcNow);
        Assert.True(coalescer.IsNewGesture(Slider));
    }

    [Fact]
    public void A_burst_coalesces_into_one_gesture()
    {
        var now = new DateTime(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);
        var coalescer = new GestureCoalescer(Gap, clock: () => now);

        Assert.True(coalescer.IsNewGesture(Slider));
        coalescer.MarkCompleted(Slider);

        now += TimeSpan.FromMilliseconds(60); // the fixture's drag debounce
        Assert.False(coalescer.IsNewGesture(Slider));
        coalescer.MarkCompleted(Slider);
    }

    [Fact]
    public void A_user_pause_past_the_gap_starts_a_new_gesture()
    {
        var now = new DateTime(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);
        var coalescer = new GestureCoalescer(Gap, clock: () => now);

        Assert.True(coalescer.IsNewGesture(Slider));
        coalescer.MarkCompleted(Slider);

        now += TimeSpan.FromSeconds(2);
        Assert.True(coalescer.IsNewGesture(Slider));
    }

    [Fact]
    public void A_slow_solve_between_pushes_of_one_drag_does_not_split_the_gesture()
    {
        // The PC-round bug: the previous implementation stamped at execution START, so a solve
        // slower than the gap made every push of a drag look like a fresh gesture. Measured
        // from COMPLETION, the pushes queue behind the solve and arrive within the gap.
        var now = new DateTime(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);
        var coalescer = new GestureCoalescer(Gap, clock: () => now);

        Assert.True(coalescer.IsNewGesture(Slider));
        now += TimeSpan.FromSeconds(3);   // the push's solve takes 3 s (172 breps)
        coalescer.MarkCompleted(Slider);

        now += TimeSpan.FromMilliseconds(5); // the queued next push executes right after
        Assert.False(coalescer.IsNewGesture(Slider));
    }

    [Fact]
    public void Controls_coalesce_independently()
    {
        var now = new DateTime(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);
        var coalescer = new GestureCoalescer(Gap, clock: () => now);
        var other = Guid.NewGuid();

        Assert.True(coalescer.IsNewGesture(Slider));
        coalescer.MarkCompleted(Slider);

        now += TimeSpan.FromMilliseconds(50);
        Assert.True(coalescer.IsNewGesture(other)); // a different control mid-burst is its own gesture
    }

    [Fact]
    public void An_open_gesture_holds_one_record_however_slow_the_pushes()
    {
        // The round-4 acceptance: a human drag's ~1 s steps each beat the quiet gap, so
        // time-gap coalescing split them into per-step records. An explicit bracket wins.
        var now = new DateTime(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);
        var coalescer = new GestureCoalescer(Gap, clock: () => now);

        coalescer.SetGesture(Slider, open: true);
        Assert.True(coalescer.IsNewGesture(Slider));   // first push opens the record
        coalescer.MarkCompleted(Slider);

        for (var i = 0; i < 4; i++)
        {
            now += TimeSpan.FromSeconds(1);            // far past the 750 ms gap
            Assert.False(coalescer.IsNewGesture(Slider));
            coalescer.MarkCompleted(Slider);
        }
    }

    [Fact]
    public void Closing_the_gesture_returns_to_gap_coalescing()
    {
        var now = new DateTime(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);
        var coalescer = new GestureCoalescer(Gap, clock: () => now);

        coalescer.SetGesture(Slider, open: true);
        Assert.True(coalescer.IsNewGesture(Slider));
        coalescer.MarkCompleted(Slider);
        coalescer.SetGesture(Slider, open: false);

        now += TimeSpan.FromSeconds(2);                // a real pause after the drag
        Assert.True(coalescer.IsNewGesture(Slider));   // next edit is its own record
    }

    [Fact]
    public void An_abandoned_gesture_expires_after_the_idle_timeout()
    {
        // A dropped pointer-up (tab crash, lost request) must not glue later edits into
        // the stale bracket forever.
        var now = new DateTime(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);
        var coalescer = new GestureCoalescer(Gap, idle: TimeSpan.FromSeconds(30), clock: () => now);

        coalescer.SetGesture(Slider, open: true);
        Assert.True(coalescer.IsNewGesture(Slider));
        coalescer.MarkCompleted(Slider);

        now += TimeSpan.FromSeconds(31);
        Assert.True(coalescer.IsNewGesture(Slider));   // expired: fresh record
        coalescer.MarkCompleted(Slider);

        now += TimeSpan.FromSeconds(1);
        Assert.False(coalescer.IsNewGesture(Slider));  // and the bracket keeps working
    }

    [Fact]
    public void Gestures_track_controls_independently()
    {
        var now = new DateTime(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);
        var coalescer = new GestureCoalescer(Gap, clock: () => now);
        var other = Guid.NewGuid();

        coalescer.SetGesture(Slider, open: true);
        Assert.True(coalescer.IsNewGesture(Slider));
        coalescer.MarkCompleted(Slider);

        now += TimeSpan.FromSeconds(1);
        Assert.True(coalescer.IsNewGesture(other));    // no bracket on the other control
        Assert.False(coalescer.IsNewGesture(Slider));  // the bracket still holds
    }
}
