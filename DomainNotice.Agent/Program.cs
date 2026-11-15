// DomainNotice.Agent — Windows service. Implemented by the Windows agent area.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "DomainNotice.Agent");
builder.Build().Run();
