using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.EpgChannelNames.Tests;

public class ChannelLogoTests
{
    private const string Logo = "/var/lib/jellyfin/metadata/livetv/abc/metadata/poster.png";

    [Fact]
    public void ProgramWithoutImage_GetsChannelLogo()
    {
        var channel = WithImage(new LiveTvChannel(), Logo);
        var program = new LiveTvProgram();

        Assert.True(ChannelRenamer.ApplyChannelLogo(program, channel));
        Assert.Equal(Logo, program.GetImageInfo(ImageType.Primary, 0)!.Path);
        Assert.False(ChannelRenamer.ApplyChannelLogo(program, channel)); // second run: no change
    }

    [Fact]
    public void ProgramWithOwnImage_IsNeverReplaced()
    {
        var channel = WithImage(new LiveTvChannel(), Logo);
        var program = WithImage(new LiveTvProgram(), "/epg/real-show.jpg");

        Assert.False(ChannelRenamer.ApplyChannelLogo(program, channel));
        Assert.Equal("/epg/real-show.jpg", program.GetImageInfo(ImageType.Primary, 0)!.Path);
    }

    [Fact]
    public void ChannelLogoChanged_IsFollowed()
    {
        var channel = WithImage(new LiveTvChannel(), Logo);
        var program = new LiveTvProgram();
        ChannelRenamer.ApplyChannelLogo(program, channel);

        WithImage(channel, "/new/poster.png");

        Assert.True(ChannelRenamer.ApplyChannelLogo(program, channel));
        Assert.Equal("/new/poster.png", program.GetImageInfo(ImageType.Primary, 0)!.Path);
    }

    [Fact]
    public void ChannelLogoGone_OurImageIsRemoved()
    {
        var channel = WithImage(new LiveTvChannel(), Logo);
        var program = new LiveTvProgram();
        ChannelRenamer.ApplyChannelLogo(program, channel);

        channel.ImageInfos = System.Array.Empty<ItemImageInfo>();

        Assert.True(ChannelRenamer.ApplyChannelLogo(program, channel));
        Assert.Null(program.GetImageInfo(ImageType.Primary, 0));
    }

    [Fact]
    public void RemoteChannelLogo_IsNotUsed()
    {
        var channel = WithImage(new LiveTvChannel(), "http://dispatcharr:9191/api/channels/logos/1/cache/");
        var program = new LiveTvProgram();

        Assert.False(ChannelRenamer.ApplyChannelLogo(program, channel));
        Assert.Null(program.GetImageInfo(ImageType.Primary, 0));
    }

    private static T WithImage<T>(T item, string path)
        where T : BaseItem
    {
        item.SetImage(new ItemImageInfo { Path = path, Type = ImageType.Primary }, 0);
        return item;
    }
}
