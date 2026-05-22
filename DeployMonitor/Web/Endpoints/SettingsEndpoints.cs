using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using DeployMonitor.ViewModels;

namespace DeployMonitor.Web.Endpoints
{
    public static class SettingsEndpoints
    {
        public static void Map(IEndpointRouteBuilder app)
        {
            app.MapGet("/api/settings", (MainViewModel vm) =>
            {
                return Results.Ok(new
                {
                    repoFolder = vm.RepoFolder,
                    deployFolder = vm.DeployFolder,
                    intervalSeconds = vm.IntervalSeconds,
                    defaultBranch = vm.DefaultBranch,
                    globalExitedOkContainers = vm.GlobalExitedOkContainers
                });
            }).RequireAuthorization();

            app.MapPut("/api/settings", (SettingsRequest req, MainViewModel vm) =>
            {
                vm.ApiUpdateSettings(req.RepoFolder, req.DeployFolder, req.IntervalSeconds, req.DefaultBranch, req.GlobalExitedOkContainers);
                return Results.Ok(new { message = "설정이 저장되었습니다." });
            }).RequireAuthorization();
        }
    }

    public record SettingsRequest(
        string? RepoFolder,
        string? DeployFolder,
        int? IntervalSeconds,
        string? DefaultBranch,
        string? GlobalExitedOkContainers);
}
