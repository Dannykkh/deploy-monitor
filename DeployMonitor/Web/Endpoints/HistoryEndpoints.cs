using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using DeployMonitor.Web.Data;

namespace DeployMonitor.Web.Endpoints
{
    public static class HistoryEndpoints
    {
        public static void Map(IEndpointRouteBuilder app)
        {
            app.MapGet("/api/history", (string? project, int? limit, DeployHistoryStore store) =>
            {
                var history = store.Query(project, limit ?? 50);
                return Results.Ok(new { history });
            }).RequireAuthorization();
        }
    }
}
