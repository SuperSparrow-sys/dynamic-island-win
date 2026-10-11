using System.IO;
using DynamicBay.Services;

namespace DynamicBay.Tests;

public class CoverLookupTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Api", "ITunes", name));

    [Fact]
    public void Picks_the_matching_song_in_600_px()
    {
        // Real answer of the iTunes Search API; the player calls the song "All of Me - Radio-Edit" by "O'Neal"
        var url = CoverLookup.PickArtwork(Fixture("search-all-of-me.json"), "All of Me - Radio-Edit", "O'Neal");
        Assert.NotNull(url);
        Assert.StartsWith("https://", url);
        Assert.Contains("600x600bb", url);
    }

    [Fact]
    public void Wrong_artist_gives_no_cover()
    {
        Assert.Null(CoverLookup.PickArtwork(Fixture("search-all-of-me.json"), "All of Me - Radio-Edit", "Metallica"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"results\":null}")]
    [InlineData("{\"results\":[null,{\"trackName\":5},{\"artworkUrl100\":null}]}")]
    public void Odd_answers_give_no_cover(string json) => Assert.Null(CoverLookup.PickArtwork(json, "Song", "Artist"));

    [Theory]
    [InlineData("Song (Radio Edit)", "Song")]
    [InlineData("Song - Remastered 2011", "Song")]
    [InlineData("Song [Live]", "Song")]
    [InlineData("Song", "Song")]
    [InlineData("(Intro)", "(Intro)")]
    public void Version_suffixes_are_dropped(string title, string clean) => Assert.Equal(clean, CoverLookup.Clean(title));

    [Fact]
    public void Search_sends_only_artist_and_title()
    {
        string url = CoverLookup.SearchUrl("All of Me (Radio Edit)", "O'Neal");
        Assert.StartsWith("https://itunes.apple.com/search?term=", url);
        Assert.Contains("All%20of%20Me", url);
        Assert.DoesNotContain("Radio", url);
    }
}
