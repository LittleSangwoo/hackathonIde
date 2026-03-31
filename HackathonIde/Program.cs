using HackathonIde.Data;
using HackathonIde.Hubs;
using HackathonIde.Models;
using HackathonIde.Services;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

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

// Эндпоинт 2: Разрешение конфликтов
app.MapPost("/api/ai/resolve-conflict", async (string codeA, string codeB, GigaChatService aiService) =>
{
    var resolvedCode = await aiService.ResolveConflictAsync(codeA, codeB);
    return Results.Ok(new { resolvedCode });
});

app.MapPost("/api/projects/{id}/execute", async (int id, AppDbContext db, IConfiguration config) =>
{
    var project = await db.Projects.FindAsync(id);
    if (project == null || string.IsNullOrWhiteSpace(project.CurrentCode))
        return Results.BadRequest("Нет кода для выполнения");

    using var client = new HttpClient();

    // Настройки для Judge0 через RapidAPI
    client.DefaultRequestHeaders.Add("X-RapidAPI-Key", config["Judge0:ApiKey"]);
    client.DefaultRequestHeaders.Add("X-RapidAPI-Host", config["Judge0:ApiHost"]);

    // Judge0 ожидает код в Base64, чтобы не было проблем со спецсимволами
    var plainTextBytes = System.Text.Encoding.UTF8.GetBytes(project.CurrentCode);
    var base64Code = Convert.ToBase64String(plainTextBytes);

    var payload = new
    {
        language_id = 51, // ID для C# (Mono 6.12.0)
        source_code = base64Code,
        stdin = "" // Входные данные, если нужны
    };

    // Отправляем запрос с параметром wait=true, чтобы сразу получить результат
    var response = await client.PostAsJsonAsync($"https://{config["Judge0:ApiHost"]}/submissions?base64_encoded=true&wait=true", payload);

    if (!response.IsSuccessStatusCode)
    {
        var error = await response.Content.ReadAsStringAsync();
        return Results.Problem($"Ошибка песочницы: {error}");
    }

    var result = await response.Content.ReadFromJsonAsync<JsonElement>();

    // Собираем вывод консоли или ошибку компиляции
    var stdout = result.TryGetProperty("stdout", out var outNode) ? outNode.GetString() : "";
    var stderr = result.TryGetProperty("stderr", out var errNode) ? errNode.GetString() : "";
    var compileError = result.TryGetProperty("compile_output", out var compNode) ? compNode.GetString() : "";

    // Декодируем из Base64 обратно в текст
    string Decode(string? base64) => string.IsNullOrEmpty(base64)
        ? ""
        : System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));

    var finalOutput = Decode(stdout) + Decode(stderr) + Decode(compileError);

    return Results.Ok(new { terminalOutput = string.IsNullOrWhiteSpace(finalOutput) ? "Программа выполнена (пустой вывод)" : finalOutput });
});

app.Run();