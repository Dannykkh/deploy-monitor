using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using DeployMonitor.ViewModels;

namespace DeployMonitor.Web.Endpoints
{
    public static class LogEndpoints
    {
        public static void Map(IEndpointRouteBuilder app)
        {
            app.MapGet("/api/logs/watch", (int? last, MainViewModel vm) =>
            {
                var logs = vm.GetWatchLogSnapshot(last ?? 100);
                return Results.Ok(new { logs });
            }).RequireAuthorization();

            app.MapGet("/api/logs/deploy", (int? last, MainViewModel vm) =>
            {
                var logs = vm.GetDeployLogSnapshot(last ?? 100);
                return Results.Ok(new { logs });
            }).RequireAuthorization();
        }
    }
}
