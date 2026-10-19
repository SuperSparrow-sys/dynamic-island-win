using DynamicBay.Services;

namespace DynamicBay.Tests;

/// <summary>Spotify and YouTube at once: the media card is a stack, and the two never take turns on their own.</summary>
public class MediaPickTests
{
    private const string Spotify = "Spotify.exe#0", Chrome = "Chrome#0";
    private static readonly DateTime T0 = new(2026, 10, 19, 20, 0, 0, DateTimeKind.Utc);

    private static MediaCandidate[] Both(bool spotifyPlays, bool chromePlays) =>
        new[] { new MediaCandidate(Spotify, spotifyPlays, true), new MediaCandidate(Chrome, chromePlays, false) };

    [Fact]
    public void Sessions_of_one_app_get_their_own_keys()
    {
        Assert.Equal(new[] { "Chrome#0", "Spotify.exe#0", "Chrome#1" }, MediaPick.Keys(new[] { "Chrome", "Spotify.exe", "Chrome" }));
    }

    [Fact]
    public void Without_a_choice_a_playing_Spotify_comes_first_then_any_playing_player()
    {
        Assert.Equal(0, MediaPick.Choose(Both(true, true), null, 1));
        Assert.Equal(1, MediaPick.Choose(Both(false, true), null, 0));
        Assert.Equal(0, MediaPick.Choose(Both(false, false), null, 1)); // nothing plays: Spotify
        Assert.Equal(-1, MediaPick.Choose(Array.Empty<MediaCandidate>(), null, -1));
    }

    [Fact]
    public void A_chosen_page_holds_while_its_player_exists()
    {
        Assert.Equal(1, MediaPick.Choose(Both(true, false), Chrome, 0)); // YouTube paused, Spotify plays: still YouTube
        Assert.Equal(0, MediaPick.Choose(new[] { new MediaCandidate(Spotify, true, true) }, Chrome, 0)); // tab closed
    }

    [Fact]
    public void A_player_that_starts_comes_to_the_front_but_a_skip_does_not_count()
    {
        var starts = new MediaStarts();
        Assert.Empty(starts.Update(Both(true, false), T0)); // what plays at startup did not just start

        // YouTube starts while Spotify plays.
        Assert.Equal(new[] { Chrome }, starts.Update(Both(true, true), T0.AddSeconds(10)));
        // Spotify skips in the background: a short stop, then it plays again. Not a start.
        Assert.Empty(starts.Update(Both(false, true), T0.AddSeconds(20)));
        Assert.Empty(starts.Update(Both(true, true), T0.AddSeconds(21)));
        // Spotify paused for a while and resumed: a start.
        Assert.Empty(starts.Update(Both(false, true), T0.AddSeconds(30)));
        Assert.Equal(new[] { Spotify }, starts.Update(Both(true, true), T0.AddSeconds(40)));
    }

    [Fact]
    public void A_new_player_that_is_paused_is_no_start()
    {
        var starts = new MediaStarts();
        starts.Update(new[] { new MediaCandidate(Spotify, true, true) }, T0);
        Assert.Empty(starts.Update(Both(true, false), T0.AddSeconds(5)));
        Assert.Equal(new[] { Chrome }, starts.Update(Both(true, true), T0.AddSeconds(15)));
    }
}
