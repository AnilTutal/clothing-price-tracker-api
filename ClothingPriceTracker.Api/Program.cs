using System.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// React (Frontend) uygulamasından gelecek isteklere izin vermek için CORS ayarı
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.RoutePrefix = "swagger";
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Clothing Price Tracker API v1");
});

app.Lifetime.ApplicationStarted.Register(() =>
{
    var baseUrl = app.Urls.FirstOrDefault();
    if (string.IsNullOrWhiteSpace(baseUrl)) return;

    var swaggerUrl = baseUrl.TrimEnd('/') + "/swagger";
    try { Process.Start(new ProcessStartInfo(swaggerUrl) { UseShellExecute = true }); }
    catch { }
});

app.UseCors("AllowFrontend");
app.UseHttpsRedirection();
app.MapControllers();
app.Run();
