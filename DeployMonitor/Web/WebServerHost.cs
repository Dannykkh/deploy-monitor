using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using DeployMonitor.ViewModels;
using DeployMonitor.Web.Auth;
using DeployMonitor.Web.Data;
using DeployMonitor.Web.Endpoints;

namespace DeployMonitor.Web
{
    public class WebServerHost
    {
        private WebApplication? _app;

        public async Task StartAsync(MainViewModel vm, int port = 5100, bool listenAnyIp = true)
        {
            try
            {
                // Initialize database
                var dbPath = SqliteDbInitializer.Initialize();

                var builder = WebApplication.CreateBuilder(new WebApplicationOptions
                {
                    ContentRootPath = AppDomain.CurrentDomain.BaseDirectory
                });

                // Kestrel binding
                builder.WebHost.ConfigureKestrel(options =>
                {
                    if (listenAnyIp)
                        options.ListenAnyIP(port);
                    else
                        options.ListenLocalhost(port);
                });

                // DI registrations
                builder.Services.AddSingleton(vm);
                var jwtHelper = new JwtHelper();
                builder.Services.AddSingleton(jwtHelper);
                builder.Services.AddSingleton(new SqliteUserStore(dbPath));
                var historyStore = new DeployHistoryStore(dbPath);
                builder.Services.AddSingleton(historyStore);

                // Wire deploy history recording
                vm.DeployHistoryStore = historyStore;

                // JWT authentication
                builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                    .AddJwtBearer(options =>
                    {
                        options.TokenValidationParameters = new TokenValidationParameters
                        {
                            ValidateIssuer = true,
                            ValidateAudience = true,
                            ValidateLifetime = true,
                            ValidateIssuerSigningKey = true,
                            ValidIssuer = "DeployMonitor",
                            ValidAudience = "DeployMonitor",
                            IssuerSigningKey = jwtHelper.GetSecurityKey()
                        };
                    });
                builder.Services.AddAuthorization();

                // Suppress default console logging
                builder.Logging.ClearProviders();

                _app = builder.Build();

                // Middleware
                _app.UseDefaultFiles();
                _app.UseStaticFiles();
                _app.UseAuthentication();
                _app.UseAuthorization();

                // Map API endpoints
                AuthEndpoints.Map(_app);
                DashboardEndpoints.Map(_app);
                ProjectEndpoints.Map(_app);
                WatchEndpoints.Map(_app);
                SettingsEndpoints.Map(_app);
                LogEndpoints.Map(_app);
                HistoryEndpoints.Map(_app);

                var bindText = listenAnyIp
                    ? $"http://<서버IP>:{port} (localhost 포함)"
                    : $"http://localhost:{port}";
                vm.AddWatchLog($"웹 대시보드 시작: {bindText}");

                await _app.StartAsync();
            }
            catch (Exception ex)
            {
                vm.AddWatchLog($"웹 대시보드 시작 실패: {ex.Message}");
            }
        }

        public async Task StopAsync()
        {
            if (_app != null)
            {
                try
                {
                    await _app.StopAsync();
                    await _app.DisposeAsync();
                }
                catch { }
                _app = null;
            }
        }
    }
}
