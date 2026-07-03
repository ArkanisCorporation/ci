const string DefaultBaseUrl = "http://localhost:5085";

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
var baseUrl = Environment.GetEnvironmentVariable("APP_BASE_URL");

if (string.IsNullOrWhiteSpace(baseUrl))
{
    baseUrl = DefaultBaseUrl;
}

app.MapGet("/", () => Results.Ok(new
{
    service = "Mock.Container.App",
    baseUrl,
}));

app.MapGet("/healthz", () => Results.Ok(new
{
    status = "ok",
}));

app.Run();
