using HackathonIde.Data;
using HackathonIde.Hubs;
using HackathonIde.Models;
using HackathonIde.Services;
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

// Настройка HttpClient для GigaChat с игнорированием SSL-ошибок (обязательно для сертификатов Сбера)
builder.Services.AddHttpClient<GigaChatService>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
    });

var app = builder.Build();

app.UseCors("AllowAll");

// 4. Маппинг хаба SignalR
app.MapHub<EditorHub>("/editorHub");

// 5. Тестовый эндпоинт, чтобы проверить, что API живо
app.MapGet("/", () => "Hackathon IDE Backend is running!");

app.MapPost("/api/projects", async (string name, AppDbContext db) =>
{
    var project = new Project { Name = name, CurrentCode = "// Happy Coding!" };
    db.Projects.Add(project);
    await db.SaveChangesAsync();
    return Results.Ok(project);
});

// Получение списка всех проектов (для дашборда) [cite: 26]
app.MapGet("/api/projects", async (AppDbContext db) =>
    await db.Projects.ToListAsync());

// Сохранение текущего состояния кода (чтобы не потерять при перезагрузке)
app.MapPut("/api/projects/{id}", async (int id, string code, AppDbContext db) =>
{
    var project = await db.Projects.FindAsync(id);
    if (project is null) return Results.NotFound();

    project.CurrentCode = code;
    await db.SaveChangesAsync();
    return Results.NoContent();
});

// Эндпоинт для AI Code Review
app.MapPost("/api/projects/{id}/review", async (int id, AppDbContext db, GigaChatService aiService) =>
{
    // 1. Находим проект в базе
    var project = await db.Projects.FindAsync(id);
    if (project == null) return Results.NotFound("Проект не найден");

    if (string.IsNullOrWhiteSpace(project.CurrentCode))
        return Results.BadRequest("Код пустой, нечего проверять");

    try
    {
        // 2. Отправляем код в GigaChat
        var reviewResult = await aiService.GetCodeReviewAsync(project.CurrentCode);

        // 3. Возвращаем результат подруге на фронтенд
        return Results.Ok(new { Suggestion = reviewResult });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Ошибка при обращении к AI: {ex.Message}");
    }
});

app.Run();