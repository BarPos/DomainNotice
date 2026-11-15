using DomainNotice.Console.Auth;
using DomainNotice.Console.DevFakes;
using DomainNotice.Console.Endpoints;
using DomainNotice.Console.Services;
using DomainNotice.Server.Abstractions;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MudBlazor;
using MudBlazor.Services;

namespace DomainNotice.Console;

/// <summary>Entry points used by DomainNotice.Server/Program.cs. Owned by the Console area.</summary>
public static class ConsoleHostingExtensions
{
    /// <summary>Paths reachable without signing in, besides [AllowAnonymous] endpoints.</summary>
    private static readonly PathString HealthPath = new("/healthz");


    /// <summary>Registers Razor components, MudBlazor, cookie authentication and authorization.</summary>
    public static IServiceCollection AddDomainNoticeConsole(this IServiceCollection services, IConfiguration config)
    {
        services.AddRazorComponents().AddInteractiveServerComponents();
        services.AddMudServices(o =>
        {
            o.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomRight;
            o.SnackbarConfiguration.VisibleStateDuration = 5000;
        });

        services.AddAuthentication(ConsoleAuth.Scheme)
            .AddCookie(ConsoleAuth.Scheme, o =>
            {
                o.Cookie.Name = ConsoleAuth.Scheme;
                o.Cookie.HttpOnly = true;
                o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                o.Cookie.SameSite = SameSiteMode.Strict;
                o.Cookie.IsEssential = true;
                o.SlidingExpiration = true;
                o.ExpireTimeSpan = TimeSpan.FromHours(8);
                o.LoginPath = ConsoleAuth.LoginPath;
                o.LogoutPath = "/account/logout";
                o.AccessDeniedPath = "/account/denied";
                o.ReturnUrlParameter = "returnUrl";
                o.Events = new CookieAuthenticationEvents { OnValidatePrincipal = ConsoleAuth.ValidatePrincipalAsync };
            });

        services.AddAuthorizationBuilder()
            // Every endpoint needs a signed-in Admin or Viewer unless it says [AllowAnonymous]. /healthz is mapped by
            // Program.cs without AllowAnonymous and must stay reachable for update.sh (it checks loopback itself).
            .SetFallbackPolicy(new AuthorizationPolicyBuilder()
                .RequireAssertion(ctx =>
                    (ctx.Resource is HttpContext http && http.Request.Path.Equals(HealthPath, StringComparison.OrdinalIgnoreCase))
                    || ctx.User.IsConsoleUser())
                .Build())
            .AddPolicy(ConsoleAuth.AdminPolicy, p => p.RequireAuthenticatedUser().RequireRole(ConsoleRoles.Admin))
            .AddPolicy(ConsoleAuth.ViewerPolicy, p => p.RequireAuthenticatedUser().RequireRole(ConsoleRoles.Admin, ConsoleRoles.Viewer));

        services.AddAntiforgery(o =>
        {
            o.Cookie.Name = "DomainNotice.Antiforgery";
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Strict;
        });

        services.AddCascadingAuthenticationState();
        services.AddScoped<AuthenticationStateProvider, ConsoleRevalidatingAuthStateProvider>();
        services.AddScoped<ConsoleUser>();
        services.AddScoped<BrowserClock>();
        services.AddScoped<AnnouncementHandoff>();

        // Development only, and only behind a flag: stand-ins for area services that are not registered (TryAdd, so
        // any real registration wins). Never in Production: a fake sign-in service there would be an auth bypass.
        if (FindEnvironment(services) is { } env && env.IsDevelopment() && config.GetValue<bool>("Console:DevFakes"))
            services.AddConsoleDevFakes();
        return services;
    }

    /// <summary>Adds the middleware (static files, auth, antiforgery) and maps the Console's endpoints and components.</summary>
    public static WebApplication UseDomainNoticeConsole(this WebApplication app)
    {
        if (app.Environment.IsDevelopment() && app.Configuration.GetValue<bool>("Console:DevCreateSchema"))
            DevSchema.EnsureTables(app.Services); // TEMPORARY until the integrator adds the initial migration.

        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/error", createScopeForErrors: true);
            app.UseHsts();
        }

        // First-run setup exists only while it is allowed (plan 7.1); otherwise it is a plain 404, also for GET.
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.Equals("/account/setup", StringComparison.OrdinalIgnoreCase))
            {
                var signIn = ctx.RequestServices.GetRequiredService<IAdminSignInService>();
                if (!await signIn.IsFirstRunSetupAllowedAsync(ctx.RequestAborted))
                {
                    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
            }
            await next();
        });

        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();

        app.MapStaticAssets().AllowAnonymous();
        app.MapConsoleEndpoints();
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
        return app;
    }

    private static IHostEnvironment? FindEnvironment(IServiceCollection services) =>
        services.LastOrDefault(d => d.ServiceType == typeof(IHostEnvironment))?.ImplementationInstance as IHostEnvironment;
}
