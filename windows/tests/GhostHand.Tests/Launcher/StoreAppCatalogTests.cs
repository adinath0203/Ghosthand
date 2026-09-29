using FluentAssertions;
using GhostHand.Platform.Launcher;
using Xunit;

namespace GhostHand.Tests.Launcher;

public class StoreAppCatalogTests
{
    private static readonly IReadOnlyList<(string Name, string Aumid)> Apps = new[]
    {
        ("Camera", "Microsoft.WindowsCamera_8wekyb3d8bbwe!App"),
        ("Photos", "Microsoft.Windows.Photos_8wekyb3d8bbwe!App"),
        ("Clock", "Microsoft.WindowsAlarms_8wekyb3d8bbwe!App"),
        ("Microsoft Store", "Microsoft.WindowsStore_8wekyb3d8bbwe!App"),
        ("Photos Legacy", "Microsoft.PhotosLegacy!App"),
        ("Spotify", "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify"),
    };

    [Theory]
    [InlineData("camera", "Microsoft.WindowsCamera_8wekyb3d8bbwe!App")]
    [InlineData("Photos", "Microsoft.Windows.Photos_8wekyb3d8bbwe!App")] // exact beats "Photos Legacy"
    [InlineData("store", "Microsoft.WindowsStore_8wekyb3d8bbwe!App")]    // "Microsoft Store"
    [InlineData("spotify", "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify")]
    public void SC01_FindsStoreAppsByDisplayName(string query, string expected)
    {
        StoreAppCatalog.FindBest(Apps, query).Should().Be(expected);
    }

    [Theory]
    [InlineData("cam")]       // too short for a partial match
    [InlineData("notepad")]   // not a Store app in this list
    public void SC02_UnknownOrAmbiguousNames_DoNotMatch(string query)
    {
        StoreAppCatalog.FindBest(Apps, query).Should().BeNull();
    }
}
