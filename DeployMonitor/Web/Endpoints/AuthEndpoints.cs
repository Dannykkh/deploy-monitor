using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using DeployMonitor.Web.Auth;

namespace DeployMonitor.Web.Endpoints
{
    public static class AuthEndpoints
    {
        public static void Map(IEndpointRouteBuilder app)
        {
            app.MapPost("/api/auth/login", (LoginRequest req, SqliteUserStore store, JwtHelper jwt) =>
            {
                if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
                    return Results.BadRequest(new { error = "사용자명과 비밀번호를 입력하세요." });

                if (!store.ValidateCredentials(req.Username, req.Password))
                    return Results.Json(new { error = "사용자명 또는 비밀번호가 올바르지 않습니다." }, statusCode: 401);

                store.UpdateLastLogin(req.Username);
                var token = jwt.GenerateToken(req.Username);
                return Results.Ok(new { token, username = req.Username });
            });

            app.MapPost("/api/auth/change-password", (ChangePasswordRequest req, SqliteUserStore store, HttpContext ctx) =>
            {
                var username = ctx.User.FindFirst(ClaimTypes.Name)?.Value;
                if (username == null) return Results.Unauthorized();

                if (string.IsNullOrWhiteSpace(req.OldPassword))
                    return Results.BadRequest(new { error = "현재 비밀번호를 입력하세요." });

                if (string.IsNullOrWhiteSpace(req.NewPassword) || req.NewPassword.Length < 4)
                    return Results.BadRequest(new { error = "새 비밀번호는 4자 이상이어야 합니다." });

                if (!store.ChangePassword(username, req.OldPassword, req.NewPassword))
                    return Results.BadRequest(new { error = "현재 비밀번호가 올바르지 않습니다." });

                return Results.Ok(new { message = "비밀번호가 변경되었습니다." });
            }).RequireAuthorization();

            app.MapPost("/api/auth/change-credentials", (ChangeCredentialsRequest req, SqliteUserStore store, JwtHelper jwt, HttpContext ctx) =>
            {
                var username = ctx.User.FindFirst(ClaimTypes.Name)?.Value;
                if (username == null) return Results.Unauthorized();

                if (string.IsNullOrWhiteSpace(req.OldPassword))
                    return Results.BadRequest(new { error = "현재 비밀번호를 입력하세요." });

                var newUsername = string.IsNullOrWhiteSpace(req.NewUsername) ? null : req.NewUsername.Trim();
                var newPassword = string.IsNullOrWhiteSpace(req.NewPassword) ? null : req.NewPassword;

                if (newUsername == null && newPassword == null)
                    return Results.BadRequest(new { error = "변경할 아이디 또는 새 비밀번호를 입력하세요." });

                if (newPassword != null && newPassword.Length < 4)
                    return Results.BadRequest(new { error = "새 비밀번호는 4자 이상이어야 합니다." });

                if (!store.ChangeCredentials(username, req.OldPassword, newUsername, newPassword, out var error, out var updatedUsername))
                    return Results.BadRequest(new { error });

                store.UpdateLastLogin(updatedUsername);
                var token = jwt.GenerateToken(updatedUsername);
                return Results.Ok(new
                {
                    message = "계정 정보가 변경되었습니다.",
                    username = updatedUsername,
                    token
                });
            }).RequireAuthorization();
        }
    }

    public record LoginRequest(string Username, string Password);
    public record ChangePasswordRequest(string OldPassword, string NewPassword);
    public record ChangeCredentialsRequest(string OldPassword, string? NewUsername, string? NewPassword);
}
