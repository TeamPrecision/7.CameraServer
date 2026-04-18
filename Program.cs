using CameraServer.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<LogService>();
builder.Services.AddSingleton<CameraService>();
builder.Services.AddHostedService(p => p.GetRequiredService<CameraService>());
builder.Services.AddControllers().AddJsonOptions(o =>
{
    o.JsonSerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter());
    o.JsonSerializerOptions.WriteIndented = true;
});
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapControllers();

// Redirect root → index.html
app.MapFallbackToFile("index.html");

app.Run();
