using DomainNotice.Server.Abstractions;
using DomainNotice.Shared;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DomainNotice.Server.Common;

internal sealed class AppLanguage(ISettingsStore settings, IAuditLog audit, ILogger<AppLanguage> logger) : IAppLanguage, IHostedService
{
    private volatile string _current = AppLanguages.Default;

    public string Current => _current;

    public event Action<string>? Changed;

    public async Task<OperationResult> SetAsync(string language, string actor, CancellationToken ct = default)
    {
        var value = language?.Trim().ToLowerInvariant();
        if (!AppLanguages.IsSupported(value)) return OperationResult.Fail("Unsupported language.");
        await settings.SetAsync(SettingKeys.Language, value, actor, ct);
        await audit.WriteAsync(actor, AuditActions.SettingChanged, "Setting", SettingKeys.Language, value, ct);
        if (value == _current) return OperationResult.Success;
        _current = value!;
        try { Changed?.Invoke(value!); }
        catch (Exception ex) { logger.LogWarning(ex, "A language change handler failed"); }
        return OperationResult.Success;
    }

    // Loaded once at start (CLI runs skip hosted services and keep English).
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _current = AppLanguages.Normalize(await settings.GetAsync(SettingKeys.Language, cancellationToken));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the language setting; using English");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
