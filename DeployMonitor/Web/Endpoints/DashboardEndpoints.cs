using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using DeployMonitor.ViewModels;

namespace DeployMonitor.Web.Endpoints
{
    public static class DashboardEndpoints
    {
        public static void Map(IEndpointRouteBuilder app)
        {
            app.MapGet("/api/dashboard", (MainViewModel vm) =>
            {
                var projects = vm.GetProjectsSnapshot();
                var (cpu, mem, gpu) = vm.GetSystemMetrics();

                return Results.Ok(new
                {
                    isWatching = vm.IsWatching,
                    system = new { cpu, mem, gpu },
                    settings = new
                    {
                        repoFolder = vm.RepoFolder,
                        deployFolder = vm.DeployFolder,
                        intervalSeconds = vm.IntervalSeconds,
                        defaultBranch = vm.DefaultBranch,
                        globalExitedOkContainers = vm.GlobalExitedOkContainers
                    },
                    projects
                });
            }).RequireAuthorization();
        }
    }
}
