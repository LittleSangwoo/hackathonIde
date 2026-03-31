using HackathonIde.Data;
using HackathonIde.Hubs;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// 1. Настройка базы данных
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

// 2. Добавление SignalR
builder.Services.AddSignalR();

// 3. Настройка CORS (разрешаем всё для хакатона)
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.SetIsOriginAllowed(_ => true) // Разрешаем любые источники
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials(); // Обязательно для SignalR
    });
});

var app = builder.Build();

app.UseCors("AllowAll");

// 4. Маппинг хаба SignalR
app.MapHub<EditorHub>("/editorHub");

// 5. Тестовый эндпоинт, чтобы проверить, что API живо
app.MapGet("/", () => "Hackathon IDE Backend is running!");

app.Run();