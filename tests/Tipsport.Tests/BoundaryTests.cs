using System.Net;
using Jellyfin.Plugin.Tipsport;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Tipsport.Tests;

public sealed class BoundaryTests
{
    [Theory]
    [InlineData("192.168.1.1")]
    [InlineData("8.8.8.8")]
    [InlineData("2001:4860:4860::8888")]
    public async Task RemoteClientsCannotCallInternalPlaylistOrMediaEvenWithSpoofedHeaders(string address)
    {
        var controller = new TipsportController(null!, null!, null!, null!, null!);
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        context.Request.Headers["X-Forwarded-For"] = "127.0.0.1";
        context.Request.Headers["X-Real-IP"] = "127.0.0.1";
        controller.ControllerContext = new() { HttpContext = context };
        var response = Assert.IsType<StatusCodeResult>(await controller.Playlist(CancellationToken.None));
        Assert.Equal(403, response.StatusCode);
        var guide = Assert.IsType<StatusCodeResult>(await controller.Guide(CancellationToken.None));
        Assert.Equal(403, guide.StatusCode);
        Assert.Equal(403, Assert.IsType<StatusCodeResult>(controller.Artwork("live")).StatusCode);
        Assert.Equal(403, Assert.IsType<StatusCodeResult>(await controller.MatchImage("live-1", CancellationToken.None)).StatusCode);
        await controller.Stream("1", CancellationToken.None);
        Assert.Equal(403, context.Response.StatusCode);
    }
    [Theory]
    [InlineData(nameof(TipsportController.Status))]
    [InlineData(nameof(TipsportController.SetSession))]
    [InlineData(nameof(TipsportController.ImportKodi))]
    [InlineData(nameof(TipsportController.TestCatalogue))]
    [InlineData(nameof(TipsportController.Stop))]
    public void ManagementEndpointsRequireAdministrator(string method)
    {
        var attributes = typeof(TipsportController).GetMethod(method)!.GetCustomAttributes(typeof(AuthorizeAttribute), true);
        var attribute = Assert.Single(attributes.Cast<AuthorizeAttribute>());
        Assert.Equal(MediaBrowser.Common.Api.Policies.RequiresElevation, attribute.Policy);
    }
}
