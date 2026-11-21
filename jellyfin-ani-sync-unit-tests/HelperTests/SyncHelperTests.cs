using System;
using jellyfin_ani_sync.Helpers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Moq;
using NUnit.Framework;

namespace jellyfin_ani_sync_unit_tests.HelperTests;

public class SyncHelperTests {
    private const string IgnoreTag = "jas-ignore";
    private Mock<ILibraryManager> _mockLibraryManager = null!;

    [SetUp]
    public void SetUp() {
        _mockLibraryManager = new Mock<ILibraryManager>();
        BaseItem.LibraryManager = _mockLibraryManager.Object;
    }

    [Test]
    public void MediaShouldBeIgnored_ReturnsFalse_WhenItemIsNull() {
        Assert.IsFalse(SyncHelper.MediaShouldBeIgnored(null!));
    }

    [Test]
    public void HasIgnoreTag_OnlyChecksItemItself_NotParents() {
        var seasonId = Guid.NewGuid();
        _mockLibraryManager.Setup(m => m.GetItemById(seasonId))
            .Returns(new Season { Tags = new[] { IgnoreTag } });
        var episode = new Episode { Tags = Array.Empty<string>(), ParentId = seasonId };

        Assert.IsFalse(SyncHelper.HasIgnoreTag(episode));
        Assert.IsTrue(SyncHelper.HasIgnoreTag(new Episode { Tags = new[] { "JAS-Ignore" } }));
        Assert.IsFalse(SyncHelper.HasIgnoreTag(null));
    }

    [Test]
    public void MediaShouldBeIgnored_ReturnsFalse_WhenNothingTagged() {
        var episode = new Episode { Tags = Array.Empty<string>() };

        Assert.IsFalse(SyncHelper.MediaShouldBeIgnored(episode));
    }

    [Test]
    public void MediaShouldBeIgnored_ReturnsFalse_ForUnrelatedTag() {
        var episode = new Episode { Tags = new[] { "favourites" } };

        Assert.IsFalse(SyncHelper.MediaShouldBeIgnored(episode));
    }

    [Test]
    public void MediaShouldBeIgnored_ReturnsTrue_WhenItemItselfTagged() {
        var episode = new Episode { Tags = new[] { IgnoreTag } };

        Assert.IsTrue(SyncHelper.MediaShouldBeIgnored(episode));
    }

    [Test]
    public void MediaShouldBeIgnored_IsCaseInsensitive() {
        var episode = new Episode { Tags = new[] { "JAS-Ignore" } };

        Assert.IsTrue(SyncHelper.MediaShouldBeIgnored(episode));
    }

    [Test]
    public void MediaShouldBeIgnored_ReturnsTrue_WhenParentSeasonTagged() {
        var seasonId = Guid.NewGuid();
        _mockLibraryManager.Setup(m => m.GetItemById(seasonId))
            .Returns(new Season { Tags = new[] { IgnoreTag } });
        var episode = new Episode { Tags = Array.Empty<string>(), ParentId = seasonId };

        Assert.IsTrue(SyncHelper.MediaShouldBeIgnored(episode));
    }

    [Test]
    public void MediaShouldBeIgnored_ReturnsTrue_WhenSeriesTagged() {
        var seriesId = Guid.NewGuid();
        _mockLibraryManager.Setup(m => m.GetItemById(seriesId))
            .Returns(new Series { Tags = new[] { IgnoreTag } });
        var episode = new Episode { Tags = Array.Empty<string>(), SeriesId = seriesId };

        Assert.IsTrue(SyncHelper.MediaShouldBeIgnored(episode));
    }

    [Test]
    public void MediaShouldBeIgnored_ReturnsFalse_WhenItemParentAndSeriesAllUntagged() {
        var seasonId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        _mockLibraryManager.Setup(m => m.GetItemById(seasonId))
            .Returns(new Season { Tags = Array.Empty<string>() });
        _mockLibraryManager.Setup(m => m.GetItemById(seriesId))
            .Returns(new Series { Tags = Array.Empty<string>() });
        var episode = new Episode { Tags = Array.Empty<string>(), ParentId = seasonId, SeriesId = seriesId };

        Assert.IsFalse(SyncHelper.MediaShouldBeIgnored(episode));
    }

    [Test]
    public void MediaShouldBeIgnored_ReturnsTrue_WhenMovieTagged() {
        var movie = new Movie { Tags = new[] { IgnoreTag } };

        Assert.IsTrue(SyncHelper.MediaShouldBeIgnored(movie));
    }

    [Test]
    public void MediaShouldBeIgnored_ReturnsFalse_WhenMovieUntagged() {
        var movie = new Movie { Tags = Array.Empty<string>() };

        Assert.IsFalse(SyncHelper.MediaShouldBeIgnored(movie));
    }
}
