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

app.UseStaticFiles();

app.UseCors("AllowAll");
app.UseStaticFiles();

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

// Получение списка всех проектов (для дашборда) 
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

    // ДОБАВЛЯЕМ ВОТ ЭТИ ДВЕ СТРОЧКИ 
    MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location), // Для работы LINQ (.Where, .OrderBy)
    MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location), // Для работы со списками (List)

    MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Runtime.dll"))
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

app.UseAuthentication();
app.UseAuthorization();

// Эндпоинт ЛОГИНА (и регистрации заодно)
app.MapPost("/api/auth/login", (AuthRequest request) =>
{
    // ТУТ ДОЛЖЕН БЫТЬ ЗАПРОС К БД (например: db.Users.FirstOrDefault(u => u.Name == request.Username))
    // Если пользователя нет - создаем, если есть - проверяем пароль. 
    // Для хакатона сделаем заглушку: пускаем всех, у кого пароль не пустой.
    if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        return Results.BadRequest(new { message = "Введите логин и пароль" });

    // Генерируем "Пропуск" (JWT Token)
    // Генерируем уникальный ID сессии "на лету"
    var userId = Guid.NewGuid().ToString();

    var claims = new[] {
    new Claim(ClaimTypes.Name, request.Username),
    new Claim(ClaimTypes.NameIdentifier, userId) // <-- Кладем ID в бейджик!
};
    var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);
    var token = new JwtSecurityToken(claims: claims, expires: DateTime.Now.AddHours(24), signingCredentials: credentials);

    var tokenString = new JwtSecurityTokenHandler().WriteToken(token);

    return Results.Ok(new { token = tokenString, username = request.Username });
});

app.MapPost("/api/projects/create", async (Project data, AppDbContext db, ClaimsPrincipal user) =>
{
    // Достаем тот самый сгенерированный ID из токена
    var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    var newProject = new Project
    {
        Name = data.Name,
        Password = data.Password,
        OwnerId = userId // Сохраняем надежный ID создателя
    };
    db.Projects.Add(newProject);
    await db.SaveChangesAsync();
    return Results.Ok(new { projectId = newProject.Id });
}).RequireAuthorization();

app.Run();