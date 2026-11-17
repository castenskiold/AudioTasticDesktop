using AudioTastic.Core.Playback;
using AudioTastic.Data.Settings;

namespace AudioTastic.Core.Tests.Playback;

/// <summary>CO-03 shuffle and CO-04 repeat.</summary>
public sealed class PlayOrderTests
{
    private static List<int> Walk(PlayOrder order, int start, int steps, bool trackEnded = true)
    {
        var visited = new List<int> { start };
        var current = start;
        for (var i = 0; i < steps && order.Next(current, trackEnded) is int next; i++)
        {
            visited.Add(next);
            current = next;
        }
        return visited;
    }

    [Fact]
    public void In_order_without_repeat_stops_after_the_last_track()
    {
        var order = new PlayOrder();
        order.Reset(3, 0);

        Assert.Equal([0, 1, 2], Walk(order, 0, 10));
        Assert.Null(order.Previous(0));
        Assert.Equal(1, order.Previous(2));
    }

    [Fact]
    public void Repeat_playlist_wraps_both_ways()
    {
        var order = new PlayOrder { Repeat = RepeatMode.Playlist };
        order.Reset(3, 0);

        Assert.Equal(0, order.Next(2, trackEnded: true));
        Assert.Equal(2, order.Previous(0));
    }

    [Fact]
    public void Repeat_track_replays_at_the_end_but_next_button_moves_on()
    {
        var order = new PlayOrder { Repeat = RepeatMode.Track };
        order.Reset(3, 0);

        Assert.Equal(1, order.Next(1, trackEnded: true));
        Assert.Equal(2, order.Next(1, trackEnded: false));
    }

    [Fact]
    public void Shuffle_plays_every_track_once_starting_with_the_current_one()
    {
        var order = new PlayOrder(new Random(42));
        order.Reset(20, 7);
        order.SetShuffle(true, 7);

        var visited = Walk(order, 7, 100);

        Assert.Equal(20, visited.Count);
        Assert.Equal(Enumerable.Range(0, 20), visited.Order());
        Assert.NotEqual(Enumerable.Range(0, 20), visited); // actually shuffled
        Assert.Equal(7, order.Order[0]);
    }

    [Fact]
    public void Shuffle_previous_walks_back_through_the_shuffled_history()
    {
        var order = new PlayOrder(new Random(1));
        order.Reset(10, 0);
        order.SetShuffle(true, 0);
        var visited = Walk(order, 0, 4);

        Assert.Equal(visited[2], order.Previous(visited[3]));
        Assert.Equal(visited[0], order.Previous(visited[1]));
        Assert.Null(order.Previous(visited[0]));
    }

    [Fact]
    public void Shuffle_with_repeat_playlist_reshuffles_without_repeating_the_last_track_first()
    {
        var order = new PlayOrder(new Random(3)) { Repeat = RepeatMode.Playlist };
        order.Reset(5, 0);
        order.SetShuffle(true, 0);
        var first = Walk(order, 0, 4);

        var next = order.Next(first[^1], trackEnded: true);

        Assert.NotNull(next);
        Assert.NotEqual(first[^1], next);
        Assert.Equal(5, order.Order.Distinct().Count());
    }

    [Fact]
    public void Empty_list_has_no_next_or_previous()
    {
        var order = new PlayOrder();
        order.Reset(0, -1);

        Assert.Null(order.Next(-1, trackEnded: true));
        Assert.Null(order.Previous(-1));
    }
}
