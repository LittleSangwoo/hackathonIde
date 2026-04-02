using Azure.Core;
using HackathonIde.Data;
using HackathonIde.Hubs;
using HackathonIde.Models;
using HackathonIde.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Reflection;
using System.Security.Claims;
using System.Text;
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
builder.Services.AddHttpClient<TelegramBotService>();
builder.Services.AddHostedService<TelegramListenerService>();
// Настройка HttpClient для GigaChat с игнорированием SSL-ошибок (обязательно для сертификатов Сбера)
builder.Services.AddHttpClient<GigaChatService>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
    });

// 1. Секретный ключ для подписи (в реальном проекте хранить в appsettings.json!)
var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("SuperSecretHackathonKey_MustBeAtLeast32BytesLong!!"));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = securityKey
        };

        // ВАЖНО ДЛЯ SIGNALR: Чтение токена из URL, когда браузер открывает WebSocket
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                // Если запрос идет к нашему хабу
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/editorHub"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseCors("AllowAll");
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

// 4. Маппинг хаба SignalR
app.MapHub<EditorHub>("/editorHub");

// 5. Тестовый эндпоинт, чтобы проверить, что API живо
//app.MapGet("/", () => "Hackathon IDE Backend is running!");

app.MapPost("/api/projects", async (string name, AppDbContext db) =>
{
    var project = new Project { Name = name, CurrentCode = "// Happy Coding!" };
    db.Projects.Add(project);
    await db.SaveChangesAsync();
    return Results.Ok(project);
});

// Получение списка всех проектов (для дашборда) 
app.MapGet("/api/projects", async (AppDbContext db) =>
    await db.Projects.ToListAsync());

// ПОЛУЧЕНИЕ СОЗДАННЫХ ПОЛЬЗОВАТЕЛЕМ КОМНАТ
app.MapGet("/api/projects/my", async (AppDbContext db, ClaimsPrincipal user) =>
{
    // Достаем ID текущего пользователя из токена
    var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    if (string.IsNullOrEmpty(userId))
        return Results.Unauthorized();

    // Ищем только те проекты, где OwnerId совпадает с ID пользователя.
    // Обязательно используем .Select, чтобы НЕ отправлять пароли на фронтенд!
    var myProjects = await db.Projects
        .Where(p => p.OwnerId == userId)
        .Select(p => new
        {
            id = p.Id,
            name = p.Name
        })
        .ToListAsync();

    return Results.Ok(myProjects);
}).RequireAuthorization();

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
app.MapPost("/api/projects/{id}/review", async (int id, ExecuteRequest request, AppDbContext db, GigaChatService aiService) =>
{
    // 1. Находим проект в базе
    var project = await db.Projects.FindAsync(id);
    if (project == null) return Results.NotFound("Проект не найден");

    if (string.IsNullOrWhiteSpace(project.CurrentCode))
        return Results.BadRequest("Код пустой, нечего проверять");

    try
    {
        // 2. Отправляем код в GigaChat
        var review = await aiService.GetCodeReviewAsync(request.Code);
        return Results.Ok(new { suggestion = review });
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

//app.MapPost("/api/projects/{id}/execute", async (int id, ExecuteRequest request) =>
//{
//    if (string.IsNullOrWhiteSpace(request.Code))
//        return Results.BadRequest("Нет кода для выполнения");

//    using var client = new HttpClient();

//    // 1. Формируем посылку для Piston API
//    var payload = new
//    {
//        language = "csharp",
//        version = "*", // Символ * заставит Piston взять последнюю стабильную версию C#
//        files = new[]
//        {
//            new { content = request.Code }
//        }
//    };

//    try
//    {
//        // 2. Отправляем код на внешний сервер компилятора
//        var response = await client.PostAsJsonAsync("https://emkc.org/api/v2/piston/execute", payload);

//        if (!response.IsSuccessStatusCode)
//        {
//            return Results.Ok(new { terminalOutput = $"Piston API недоступен. Статус: {response.StatusCode}" });
//        }

//        // 3. Читаем ответ. Используем JsonNode, чтобы не писать лишние классы-модели
//        var result = await response.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>();

//        // Достаем текст из поля run -> output
//        var output = result?["run"]?["output"]?.ToString();

//        return Results.Ok(new { terminalOutput = string.IsNullOrWhiteSpace(output) ? "Программа выполнена (вывода нет)" : output });
//    }
//    catch (Exception ex)
//    {
//        return Results.Ok(new { terminalOutput = $"Ошибка связи с сервером компиляции: {ex.Message}" });
//    }
//});

app.MapPost("/api/projects/{id}/execute", async (int id, ExecuteRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Code))
        return Results.BadRequest("Нет кода для выполнения");

    try
    {
        // 1. Парсим текст в синтаксическое дерево (можно передать массив деревьев, если файлов несколько!)
        var syntaxTree = CSharpSyntaxTree.ParseText(request.Code);

        // 2. Собираем базовые библиотеки (.NET Core)
        string assemblyPath = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var references = new List<MetadataReference>
{
    MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
    MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
    MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
    MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
    MetadataReference.CreateFromFile(typeof(System.Text.Json.JsonSerializer).Assembly.Location),
    
    // Ссылка на Microsoft.CSharp (ты её уже добавила, но на всякий случай через Binder)
    MetadataReference.CreateFromFile(typeof(Microsoft.CSharp.RuntimeBinder.Binder).Assembly.Location),

    // ФИКС ТЕКУЩЕЙ ОШИБКИ: Библиотеки для работы динамических вызовов и Expressions
    MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Linq.Expressions.dll")),
    MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Dynamic.Runtime.dll")),

    // Базовые системные файлы
    MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Runtime.dll")),
    MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Collections.dll"))
};

        // 3. Создаем КОМПИЛЯЦИЮ (говорим, что это консольное приложение)
        var compilation = CSharpCompilation.Create(
            "HackathonProject",
            new[] { syntaxTree },
            references,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication)); // <-- Важный момент!

        // 4. Компилируем прямо в поток памяти
        using var ms = new MemoryStream();
        var emitResult = compilation.Emit(ms);

        // 5. Если есть ошибки компиляции (кто-то забыл точку с запятой)
        if (!emitResult.Success)
        {
            var errors = string.Join("\n", emitResult.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => $"Строка {d.Location.GetLineSpan().StartLinePosition.Line + 1}: {d.GetMessage()}"));
            return Results.Ok(new { terminalOutput = $"Ошибки сборки:\n{errors}" });
        }

        // 6. Если скомпилировалось — ЗАПУСКАЕМ!
        ms.Seek(0, SeekOrigin.Begin);
        var assembly = Assembly.Load(ms.ToArray());
        var entryPoint = assembly.EntryPoint; // Roslyn сам найдет метод Main()

        if (entryPoint == null)
            return Results.Ok(new { terminalOutput = "Ошибка: Не найден метод static void Main()" });

        var sw = new StringWriter();
        Console.SetOut(sw);

        // Вызываем метод Main
        var parameters = entryPoint.GetParameters().Length == 0 ? null : new object[] { Array.Empty<string>() };
        entryPoint.Invoke(null, parameters);

        var output = sw.ToString();
        return Results.Ok(new { terminalOutput = string.IsNullOrEmpty(output) ? "Выполнено успешно." : output });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { terminalOutput = $"Ошибка: {ex.Message}" });
    }
    finally
    {
        var standardOutput = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(standardOutput);
    }



});

//app.MapPost("/api/projects/{id}/execute", async (int id, ExecuteRequest request, IConfiguration config) =>
//{
//    // 1. Проверяем, не пустой ли код пришел от фронтенда
//    if (string.IsNullOrWhiteSpace(request.Code))
//        return Results.BadRequest("Нет кода для выполнения");

//    // 2. Достаем ключи из безопасного хранилища
//    var clientId = config["JDoodle:ClientId"];
//    var clientSecret = config["JDoodle:ClientSecret"];

//    if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
//        return Results.Problem("Ошибка сервера: не настроены ключи JDoodle API");

//    // 3. Собираем посылку строго по документации JDoodle
//    var payload = new
//    {
//        clientId = clientId,
//        clientSecret = clientSecret,
//        script = request.Code,
//        language = "csharp",
//        versionIndex = "5" // Индекс "5" означает использование свежего Mono / .NET
//    };

//    try
//    {
//        using var client = new HttpClient();

//        // 4. Отправляем код на компиляцию в облако
//        var response = await client.PostAsJsonAsync("https://api.jdoodle.com/v1/execute", payload);

//        // 5. Читаем ответ от сервера
//        var result = await response.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>();

//        if (!response.IsSuccessStatusCode)
//        {
//            var errorMsg = result?["error"]?.ToString() ?? response.StatusCode.ToString();
//            return Results.Ok(new { terminalOutput = $"Ошибка сервиса JDoodle: {errorMsg}" });
//        }

//        // 6. Достаем результат работы программы из поля "output"
//        var output = result?["output"]?.ToString();

//        return Results.Ok(new { terminalOutput = string.IsNullOrWhiteSpace(output) ? "Программа выполнена (вывода нет)" : output });
//    }
//    catch (Exception ex)
//    {
//        // Перехватываем ошибки сети (например, если пропал интернет)
//        return Results.Ok(new { terminalOutput = $"Ошибка сети при вызове компилятора: {ex.Message}" });
//    }
//});

// РЕГИСТРАЦИЯ
app.MapPost("/api/auth/register", async (AuthRequest request, AppDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        return Results.BadRequest(new { message = "Логин и пароль не могут быть пустыми" });

    if (await db.Users.AnyAsync(u => u.Username == request.Username))
        return Results.Conflict(new { message = "Пользователь с таким именем уже существует" });

    var user = new User
    {
        Username = request.Username,
        Password= request.Password
    };

    db.Users.Add(user);
    await db.SaveChangesAsync();

    return Results.Ok(new { message = "Регистрация успешна" });
});

// ЛОГИН (обновленный)
app.MapPost("/api/auth/login", async (AuthRequest request, AppDbContext db, ILogger<Program> logger) =>
{
    logger.LogInformation("Попытка входа: {Username}", request.Username);
    var user = await db.Users.FirstOrDefaultAsync(u => u.Username == request.Username);

    if (user == null || user.Password != request.Password)
        return Results.Unauthorized(); // 401 Unauthorized

    // Кладем в токен Id пользователя и его имя
    var claims = new[] {
        new Claim(ClaimTypes.Name, user.Username),
        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())
    };

    var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);
    var token = new JwtSecurityToken(claims: claims, expires: DateTime.Now.AddHours(24), signingCredentials: credentials);
    var tokenString = new JwtSecurityTokenHandler().WriteToken(token);

    return Results.Ok(new { token = tokenString, username = user.Username, userId = user.Id });
});

// СОЗДАНИЕ КОМНАТЫ (Обновлено: пароль теперь обязателен)
app.MapPost("/api/projects/create", async (Project data, AppDbContext db, ClaimsPrincipal user) =>
{
    var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    // 1. Проверка на пустое имя (хорошая практика)
    if (string.IsNullOrWhiteSpace(data.Name))
        return Results.BadRequest(new { message = "Название комнаты не может быть пустым" });

    // 2. НОВАЯ ПРОВЕРКА: Пароль обязателен!
    if (string.IsNullOrWhiteSpace(data.Password))
        return Results.BadRequest(new { message = "Пароль обязателен для создания комнаты!" });

    // 3. Проверка на уникальность имени (которую мы добавили ранее)
    if (await db.Projects.AnyAsync(p => p.Name == data.Name))
        return Results.Conflict(new { message = "Комната с таким названием уже существует" });

    var newProject = new Project
    {
        Name = data.Name,
        Password = data.Password,
        OwnerId = userId,
        CurrentCode = "// Happy Coding!"
    };

    db.Projects.Add(newProject);
    await db.SaveChangesAsync();

    return Results.Ok(new { projectId = newProject.Id, message = "Проект создан" });
}).RequireAuthorization();

// ВХОД В КОМНАТУ (Проверка пароля перед подключением к сокетам)
app.MapPost("/api/projects/{id}/join", async (int id, JoinProjectRequest request, AppDbContext db, ClaimsPrincipal user) =>
{
    var project = await db.Projects.FindAsync(id);
    if (project == null) return Results.NotFound(new { message = "Проект не найден" });

    // Проверяем пароль (если проект с паролем)
    if (!string.IsNullOrEmpty(project.Password) && project.Password != request.Password)
    {
        // Разрешаем войти без пароля, если это создатель комнаты
        var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (project.OwnerId != userId)
        {
            return Results.Unauthorized(); // Пароль неверный
        }
    }

    // Если всё ок, возвращаем текущий код проекта, чтобы фронтенд сразу его загрузил
    return Results.Ok(new
    {
        message = "Доступ разрешен",
        currentCode = project.CurrentCode
    });
}).RequireAuthorization();

app.MapDelete("/api/projects/{id}", async (int id, AppDbContext db, ClaimsPrincipal user) =>
{
    var project = await db.Projects.FindAsync(id);
    if (project == null) return Results.NotFound();

    var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    if (project.OwnerId != userId) return Results.Forbid();

    db.Projects.Remove(project);
    await db.SaveChangesAsync();
    return Results.Ok(new { message = "Проект удален" });
}).RequireAuthorization();

app.Run();