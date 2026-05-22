using System;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using DeployMonitor.ViewModels;
using DeployMonitor.Services;

namespace DeployMonitor.Web.Endpoints
{
    public static class ProjectEndpoints
    {
        public static void Map(IEndpointRouteBuilder app)
        {
            app.MapPost("/api/projects/{name}/deploy", async (string name, MainViewModel vm) =>
            {
                var result = await vm.ApiManualDeploy(name);
                return result
                    ? Results.Ok(new { message = $"{name} 배포가 시작되었습니다." })
                    : Results.NotFound(new { error = $"프로젝트 '{name}'을 찾을 수 없습니다." });
            }).RequireAuthorization();

            app.MapGet("/api/projects/{name}/log", (string name, MainViewModel vm) =>
            {
                var log = vm.GetProjectLog(name);
                return log != null
                    ? Results.Ok(new { projectName = name, log })
                    : Results.NotFound(new { error = $"프로젝트 '{name}'을 찾을 수 없습니다." });
            }).RequireAuthorization();

            app.MapGet("/api/projects/{name}/containers", async (string name, MainViewModel vm) =>
            {
                var project = vm.GetProject(name);
                if (project == null)
                    return Results.NotFound(new { error = $"프로젝트 '{name}'을 찾을 수 없습니다." });

                var details = await DockerInspectorService.GetProjectContainersAsync(project, vm.GlobalExitedOkContainers);
                return Results.Ok(new
                {
                    projectName = details.ProjectName,
                    matchedPrefix = details.MatchedPrefix,
                    triedPrefixes = details.TriedPrefixes,
                    runningCount = details.RunningCount,
                    expectedStoppedCount = details.ExpectedStoppedCount,
                    errorCount = details.ErrorCount,
                    success = details.Success,
                    message = details.Message,
                    containers = details.Containers.Select(c => new
                    {
                        name = c.Name,
                        status = c.Status,
                        state = c.State,
                        level = c.Level
                    })
                });
            }).RequireAuthorization();

            app.MapDelete("/api/projects/{name}/containers/{container}", async (
                string name,
                string container,
                MainViewModel vm) =>
            {
                var project = vm.GetProject(name);
                if (project == null)
                    return Results.NotFound(new { error = $"프로젝트 '{name}'을 찾을 수 없습니다." });

                // 프로젝트 소속 컨테이너 검증
                var details = await DockerInspectorService.GetProjectContainersAsync(project, vm.GlobalExitedOkContainers);
                var isProjectContainer = details.Containers.Any(c =>
                    string.Equals(c.Name, container, StringComparison.OrdinalIgnoreCase));
                if (!isProjectContainer)
                {
                    return Results.BadRequest(new
                    {
                        error = $"컨테이너 '{container}'는 프로젝트 '{name}'에 매칭되지 않습니다."
                    });
                }

                var (success, error) = await DockerInspectorService.RemoveContainerAsync(container);
                if (!success)
                    return Results.BadRequest(new { error });

                return Results.Ok(new { message = $"컨테이너 '{container}'가 삭제되었습니다." });
            }).RequireAuthorization();

            app.MapGet("/api/projects/{name}/containers/{container}/logs", async (
                string name,
                string container,
                int? tail,
                MainViewModel vm) =>
            {
                var project = vm.GetProject(name);
                if (project == null)
                    return Results.NotFound(new { error = $"프로젝트 '{name}'을 찾을 수 없습니다." });

                var details = await DockerInspectorService.GetProjectContainersAsync(project, vm.GlobalExitedOkContainers);
                var isProjectContainer = details.Containers.Any(c =>
                    string.Equals(c.Name, container, StringComparison.OrdinalIgnoreCase));
                if (!isProjectContainer)
                {
                    return Results.BadRequest(new
                    {
                        error = $"컨테이너 '{container}'는 프로젝트 '{name}'에 매칭되지 않습니다."
                    });
                }

                var res = await DockerInspectorService.GetContainerLogsAsync(container, tail ?? 120);
                if (!res.success)
                    return Results.BadRequest(new { error = res.error });

                return Results.Ok(new
                {
                    projectName = name,
                    containerName = container,
                    tail = tail ?? 120,
                    logs = res.logs
                });
            }).RequireAuthorization();
        }
    }
}
