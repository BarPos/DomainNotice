using DomainNotice.Shared;
using Microsoft.AspNetCore.Localization;
using System.Globalization;
using DomainNotice.Console.Auth;
using DomainNotice.Console.Endpoints;
using DomainNotice.Console.Services;
using DomainNotice.Server.Abstractions;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using DomainNotice.Server.Options;
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

        // HTTPS-only Console: cookies are always Secure. When it also listens on plain HTTP (--console-mode=http, or
        // behind an HTTPS proxy), cookies follow the request so that signing in over HTTP works.
        var urls = config.GetSection(ConsoleOptions.Section).Get<ConsoleOptions>()?.Urls ?? "";
        var cookieSecurity = urls.Contains("http://", StringComparison.OrdinalIgnoreCase)
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;

        services.AddAuthentication(ConsoleAuth.Scheme)
            .AddCookie(ConsoleAuth.Scheme, o =>
            {
                o.Cookie.Name = ConsoleAuth.Scheme;
                o.Cookie.HttpOnly = true;
                o.Cookie.SecurePolicy = cookieSecurity;
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
            o.Cookie.SecurePolicy = cookieSecurity;
            o.Cookie.SameSite = SameSiteMode.Strict;
        });

        services.AddCascadingAuthenticationState();
        services.AddScoped<AuthenticationStateProvider, ConsoleRevalidatingAuthStateProvider>();
        services.AddScoped<ConsoleUser>();
        services.AddScoped<BrowserClock>();
        services.AddSingleton<Texts>();
        services.AddScoped<AnnouncementHandoff>();

        return services;
    }

    /// <summary>Adds the middleware (static files, auth, antiforgery) and maps the Console's endpoints and components.</summary>
    public static WebApplication UseDomainNoticeConsole(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/error", createScopeForErrors: true);
            app.UseHsts();
        }

        // Dates, numbers and built-in component texts follow the installation's language (Settings → General).
        // Blazor circuits take the culture of the request that starts them, so a language change applies on reload.
        var cultures = AppLanguages.All.Select(l => new CultureInfo(AppLanguages.CultureName(l))).ToList();
        app.UseRequestLocalization(o =>
        {
            o.DefaultRequestCulture = new RequestCulture(AppLanguages.CultureName(AppLanguages.Default));
            o.SupportedCultures = cultures;
            o.SupportedUICultures = cultures;
            o.RequestCultureProviders.Clear();
            o.RequestCultureProviders.Add(new CustomRequestCultureProvider(ctx =>
            {
                var language = ctx.RequestServices.GetRequiredService<IAppLanguage>().Current;
                return Task.FromResult<ProviderCultureResult?>(new ProviderCultureResult(AppLanguages.CultureName(language)));
            }));
        });

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
}
