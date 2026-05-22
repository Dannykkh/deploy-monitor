using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using DeployMonitor.ViewModels;

namespace DeployMonitor.Web.Endpoints
{
    public static class WatchEndpoints
    {
        public static void Map(IEndpointRouteBuilder app)
        {
            app.MapPost("/api/watch/start", async (MainViewModel vm) =>
            {
                await vm.ApiStartWatch();
                return Results.Ok(new { message = "감시가 시작되었습니다.", isWatching = true });
            }).RequireAuthorization();

            app.MapPost("/api/watch/stop", async (MainViewModel vm) =>
            {
                await vm.ApiStopWatch();
                return Results.Ok(new { message = "감시가 중지되었습니다.", isWatching = false });
            }).RequireAuthorization();

            app.MapPost("/api/watch/refresh", async (MainViewModel vm) =>
            {
                await vm.ApiScanProjects();
                return Results.Ok(new { message = "프로젝트 새로고침이 시작되었습니다." });
            }).RequireAuthorization();
        }
    }
}
