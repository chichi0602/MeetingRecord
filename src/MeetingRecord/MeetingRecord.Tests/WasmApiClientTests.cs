using System.Net;
using Microsoft.AspNetCore.Components;
using MeetingRecord.Dtos.Uploads;
using MeetingRecord.Web.Client.Api;

namespace MeetingRecord.Tests;

/// <summary>
/// WASM 端的 <see cref="ApiClient"/>（1.1.23）：連不上伺服器要回失敗結果、不丟例外（以前丟出來，整頁被錯誤邊界換成錯誤畫面）；
/// 401 整頁導到登入頁之前要先通知（上傳中的「確定要離開嗎？」要先拿掉，不然瀏覽器會擋下導向）。
/// </summary>
public sealed class WasmApiClientTests
{
    [Fact]
    public async Task NetworkFailure_ShouldReturnFailureResult_InsteadOfThrowing()
    {
        var navigation = new TestNavigationManager();
        var client = new ApiClient(new HttpClient(new ThrowingHandler()) { BaseAddress = new Uri("http://localhost/") }, navigation);

        var get = await client.GetAsync("api/v1/jobs", ClientJsonContext.Default.BackgroundJobsResult);
        var post = await client.PostAsync("api/v1/uploads", new UploadSessionRequestDto(), ClientJsonContext.Default.UploadSessionRequestDto, ClientJsonContext.Default.UploadSessionResult);
        var delete = await client.DeleteAsync("api/v1/uploads/x", ClientJsonContext.Default.EmptyResult);
        var download = await client.DownloadAsync(new HttpRequestMessage(HttpMethod.Get, "api/v1/Meeting/1/transcript/file"));

        foreach (var result in new[] { (get.Success, get.StatusCode, get.ErrorMessage), (post.Success, post.StatusCode, post.ErrorMessage), (delete.Success, delete.StatusCode, delete.ErrorMessage) })
        {
            Assert.False(result.Success);
            Assert.Equal(ApiClient.NetworkErrorStatusCode, result.StatusCode);
            Assert.Equal(ApiClient.NetworkErrorMessage, result.ErrorMessage);
        }

        Assert.False(download.Success);
        Assert.Equal(ApiClient.NetworkErrorMessage, download.Error);
        Assert.Null(navigation.NavigatedTo);
    }

    [Fact]
    public async Task Unauthorized_ShouldNotifyBeforeRedirectingToLogin()
    {
        var navigation = new TestNavigationManager();
        var client = new ApiClient(new HttpClient(new StatusHandler(HttpStatusCode.Unauthorized)) { BaseAddress = new Uri("http://localhost/") }, navigation);
        string? navigatedWhenNotified = "not called";
        client.BeforeLoginRedirect += () =>
        {
            navigatedWhenNotified = navigation.NavigatedTo;
            return Task.CompletedTask;
        };

        var result = await client.GetAsync("api/v1/jobs", ClientJsonContext.Default.BackgroundJobsResult);

        Assert.Equal(401, result.StatusCode);
        Assert.Null(navigatedWhenNotified);
        Assert.Equal(ApiClient.LoginPath, navigation.NavigatedTo);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("TypeError: Failed to fetch");
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(string.Empty) });
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("http://localhost/", "http://localhost/meetings");

        public string? NavigatedTo { get; private set; }

        protected override void NavigateToCore(string uri, NavigationOptions options) => NavigatedTo = uri;
    }
}
