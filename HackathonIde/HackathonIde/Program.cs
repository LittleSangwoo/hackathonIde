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

// 1. Íàñòðîéêà áàçû äàííûõ
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

// 2. Äîáàâëåíèå SignalR
builder.Services.AddSignalR();

// 3. Íàñòðîéêà CORS (ðàçðåøàåì âñ¸ äëÿ õàêàòîíà)
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.SetIsOriginAllowed(_ => true) // Ðàçðåøàåì ëþáûå èñòî÷íèêè
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials(); // Îáÿçàòåëüíî äëÿ SignalR
    });
});
builder.Services.AddHttpClient<TelegramBotService>();
builder.Services.AddHostedService<TelegramListenerService>();
// Íàñòðîéêà HttpClient äëÿ GigaChat ñ èãíîðèðîâàíèåì SSL-îøèáîê (îáÿçàòåëüíî äëÿ ñåðòèôèêàòîâ Ñáåðà)
builder.Services.AddHttpClient<GigaChatService>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
    });

// 1. Ñåêðåòíûé êëþ÷ äëÿ ïîäïèñè (â ðåàëüíîì ïðîåêòå õðàíèòü â appsettings.json!)
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

        // ÂÀÆÍÎ ÄËß SIGNALR: ×òåíèå òîêåíà èç URL, êîãäà áðàóçåð îòêðûâàåò WebSocket
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                // Åñëè çàïðîñ èäåò ê íàøåìó õàáó
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

// 4. Ìàïïèíã õàáà SignalR
app.MapHub<EditorHub>("/editorHub");

// 5. Òåñòîâûé ýíäïîèíò, ÷òîáû ïðîâåðèòü, ÷òî API æèâî
//app.MapGet("/", () => "Hackathon IDE Backend is running!");

app.MapPost("/api/projects", async (string name, AppDbContext db) =>
{
    var project = new Project { Name = name, CurrentCode = "// Happy Coding!" };
    db.Projects.Add(project);
    await db.SaveChangesAsync();
    return Results.Ok(project);
});

// Ïîëó÷åíèå ñïèñêà âñåõ ïðîåêòîâ (äëÿ äàøáîðäà) 
app.MapGet("/api/projects", async (AppDbContext db) =>
    await db.Projects.ToListAsync());

// ÏÎËÓ×ÅÍÈÅ ÑÎÇÄÀÍÍÛÕ ÏÎËÜÇÎÂÀÒÅËÅÌ ÊÎÌÍÀÒ
app.MapGet("/api/projects/my", async (AppDbContext db, ClaimsPrincipal user) =>
{
    // Äîñòàåì ID òåêóùåãî ïîëüçîâàòåëÿ èç òîêåíà
    var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    if (string.IsNullOrEmpty(userId))
        return Results.Unauthorized();

    // Èùåì òîëüêî òå ïðîåêòû, ãäå OwnerId ñîâïàäàåò ñ ID ïîëüçîâàòåëÿ.
    // Îáÿçàòåëüíî èñïîëüçóåì .Select, ÷òîáû ÍÅ îòïðàâëÿòü ïàðîëè íà ôðîíòåíä!
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

// Ñîõðàíåíèå òåêóùåãî ñîñòîÿíèÿ êîäà (÷òîáû íå ïîòåðÿòü ïðè ïåðåçàãðóçêå)
app.MapPut("/api/projects/{id}", async (int id, string code, AppDbContext db) =>
{
    var project = await db.Projects.FindAsync(id);
    if (project is null) return Results.NotFound();

    project.CurrentCode = code;
    await db.SaveChangesAsync();
    return Results.NoContent();
});

// Ýíäïîèíò äëÿ AI Code Review
app.MapPost("/api/projects/{id}/review", async (int id, ExecuteRequest request, AppDbContext db, GigaChatService aiService) =>
{
    // 1. Íàõîäèì ïðîåêò â áàçå
    var project = await db.Projects.FindAsync(id);
    if (project == null) return Results.NotFound("Ïðîåêò íå íàéäåí");

    if (string.IsNullOrWhiteSpace(project.CurrentCode))
        return Results.BadRequest("Êîä ïóñòîé, íå÷åãî ïðîâåðÿòü");

    try
    {
        // 2. Îòïðàâëÿåì êîä â GigaChat
        var review = await aiService.GetCodeReviewAsync(request.Code);
        return Results.Ok(new { suggestion = review });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Îøèáêà ïðè îáðàùåíèè ê AI: {ex.Message}");
    }
});

// Ýíäïîèíò 2: Ðàçðåøåíèå êîíôëèêòîâ
app.MapPost("/api/ai/resolve-conflict", async (string codeA, string codeB, GigaChatService aiService) =>
{
    var resolvedCode = await aiService.ResolveConflictAsync(codeA, codeB);
    return Results.Ok(new { resolvedCode });
});

//app.MapPost("/api/projects/{id}/execute", async (int id, ExecuteRequest request) =>
//{
//    if (string.IsNullOrWhiteSpace(request.Code))
//        return Results.BadRequest("Íåò êîäà äëÿ âûïîëíåíèÿ");

//    using var client = new HttpClient();

//    // 1. Ôîðìèðóåì ïîñûëêó äëÿ Piston API
//    var payload = new
//    {
//        language = "csharp",
//        version = "*", // Ñèìâîë * çàñòàâèò Piston âçÿòü ïîñëåäíþþ ñòàáèëüíóþ âåðñèþ C#
//        files = new[]
//        {
//            new { content = request.Code }
//        }
//    };

//    try
//    {
//        // 2. Îòïðàâëÿåì êîä íà âíåøíèé ñåðâåð êîìïèëÿòîðà
//        var response = await client.PostAsJsonAsync("https://emkc.org/api/v2/piston/execute", payload);

//        if (!response.IsSuccessStatusCode)
//        {
//            return Results.Ok(new { terminalOutput = $"Piston API íåäîñòóïåí. Ñòàòóñ: {response.StatusCode}" });
//        }

//        // 3. ×èòàåì îòâåò. Èñïîëüçóåì JsonNode, ÷òîáû íå ïèñàòü ëèøíèå êëàññû-ìîäåëè
//        var result = await response.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>();

//        // Äîñòàåì òåêñò èç ïîëÿ run -> output
//        var output = result?["run"]?["output"]?.ToString();

//        return Results.Ok(new { terminalOutput = string.IsNullOrWhiteSpace(output) ? "Ïðîãðàììà âûïîëíåíà (âûâîäà íåò)" : output });
//    }
//    catch (Exception ex)
//    {
//        return Results.Ok(new { terminalOutput = $"Îøèáêà ñâÿçè ñ ñåðâåðîì êîìïèëÿöèè: {ex.Message}" });
//    }
//});

// app.MapPost("/api/projects/{id}/execute", async (int id, ExecuteRequest request) =>
// {
//     if (string.IsNullOrWhiteSpace(request.Code))
//         return Results.BadRequest("Íåò êîäà äëÿ âûïîëíåíèÿ");

//     try
//     {
//         // 1. Ïàðñèì òåêñò â ñèíòàêñè÷åñêîå äåðåâî (ìîæíî ïåðåäàòü ìàññèâ äåðåâüåâ, åñëè ôàéëîâ íåñêîëüêî!)
//         var syntaxTree = CSharpSyntaxTree.ParseText(request.Code);

//         // 2. Ñîáèðàåì áàçîâûå áèáëèîòåêè (.NET Core)
//         string assemblyPath = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
//         var references = new List<MetadataReference>
// {
//     MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
//     MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
//     MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
//     MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
//     MetadataReference.CreateFromFile(typeof(System.Text.Json.JsonSerializer).Assembly.Location),
    
//     // Ññûëêà íà Microsoft.CSharp (òû å¸ óæå äîáàâèëà, íî íà âñÿêèé ñëó÷àé ÷åðåç Binder)
//     MetadataReference.CreateFromFile(typeof(Microsoft.CSharp.RuntimeBinder.Binder).Assembly.Location),

//     // ÔÈÊÑ ÒÅÊÓÙÅÉ ÎØÈÁÊÈ: Áèáëèîòåêè äëÿ ðàáîòû äèíàìè÷åñêèõ âûçîâîâ è Expressions
//     MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Linq.Expressions.dll")),
//     MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Dynamic.Runtime.dll")),

//     // Áàçîâûå ñèñòåìíûå ôàéëû
//     MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Runtime.dll")),
//     MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Collections.dll"))
// };

//         // 3. Ñîçäàåì ÊÎÌÏÈËßÖÈÞ (ãîâîðèì, ÷òî ýòî êîíñîëüíîå ïðèëîæåíèå)
//         var compilation = CSharpCompilation.Create(
//             "HackathonProject",
//             new[] { syntaxTree },
//             references,
//             new CSharpCompilationOptions(OutputKind.ConsoleApplication)); // <-- Âàæíûé ìîìåíò!

//         // 4. Êîìïèëèðóåì ïðÿìî â ïîòîê ïàìÿòè
//         using var ms = new MemoryStream();
//         var emitResult = compilation.Emit(ms);

//         // 5. Åñëè åñòü îøèáêè êîìïèëÿöèè (êòî-òî çàáûë òî÷êó ñ çàïÿòîé)
//         if (!emitResult.Success)
//         {
//             var errors = string.Join("\n", emitResult.Diagnostics
//                 .Where(d => d.Severity == DiagnosticSeverity.Error)
//                 .Select(d => $"Ñòðîêà {d.Location.GetLineSpan().StartLinePosition.Line + 1}: {d.GetMessage()}"));
//             return Results.Ok(new { terminalOutput = $"Îøèáêè ñáîðêè:\n{errors}" });
//         }

//         // 6. Åñëè ñêîìïèëèðîâàëîñü  ÇÀÏÓÑÊÀÅÌ!
//         ms.Seek(0, SeekOrigin.Begin);
//         var assembly = Assembly.Load(ms.ToArray());
//         var entryPoint = assembly.EntryPoint; // Roslyn ñàì íàéäåò ìåòîä Main()

//         if (entryPoint == null)
//             return Results.Ok(new { terminalOutput = "Îøèáêà: Íå íàéäåí ìåòîä static void Main()" });

//         var sw = new StringWriter();
//         Console.SetOut(sw);

//         // Âûçûâàåì ìåòîä Main
//         var parameters = entryPoint.GetParameters().Length == 0 ? null : new object[] { Array.Empty<string>() };
//         entryPoint.Invoke(null, parameters);

//         var output = sw.ToString();
//         return Results.Ok(new { terminalOutput = string.IsNullOrEmpty(output) ? "Âûïîëíåíî óñïåøíî." : output });
//     }
//     catch (Exception ex)
//     {
//         return Results.Ok(new { terminalOutput = $"Îøèáêà: {ex.Message}" });
//     }
//     finally
//     {
//         var standardOutput = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
//         Console.SetOut(standardOutput);
//     }



// });

app.MapPost("/api/projects/{id}/execute", async (int id, ExecuteRequest request, IConfiguration config) =>
{
   // 1. Ïðîâåðÿåì, íå ïóñòîé ëè êîä ïðèøåë îò ôðîíòåíäà
   if (string.IsNullOrWhiteSpace(request.Code))
       return Results.BadRequest("Íåò êîäà äëÿ âûïîëíåíèÿ");

   // 2. Äîñòàåì êëþ÷è èç áåçîïàñíîãî õðàíèëèùà
   var clientId = config["JDoodle:ClientId"];
   var clientSecret = config["JDoodle:ClientSecret"];

   if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
       return Results.Problem("Îøèáêà ñåðâåðà: íå íàñòðîåíû êëþ÷è JDoodle API");

   // 3. Ñîáèðàåì ïîñûëêó ñòðîãî ïî äîêóìåíòàöèè JDoodle
   var payload = new
   {
       clientId = clientId,
       clientSecret = clientSecret,
       script = request.Code,
       language = "csharp",
       versionIndex = "5" // Èíäåêñ "5" îçíà÷àåò èñïîëüçîâàíèå ñâåæåãî Mono / .NET
   };

   try
   {
       using var client = new HttpClient();

       // 4. Îòïðàâëÿåì êîä íà êîìïèëÿöèþ â îáëàêî
       var response = await client.PostAsJsonAsync("https://api.jdoodle.com/v1/execute", payload);

       // 5. ×èòàåì îòâåò îò ñåðâåðà
       var result = await response.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>();

       if (!response.IsSuccessStatusCode)
       {
           var errorMsg = result?["error"]?.ToString() ?? response.StatusCode.ToString();
           return Results.Ok(new { terminalOutput = $"Îøèáêà ñåðâèñà JDoodle: {errorMsg}" });
       }

       // 6. Äîñòàåì ðåçóëüòàò ðàáîòû ïðîãðàììû èç ïîëÿ "output"
       var output = result?["output"]?.ToString();

       return Results.Ok(new { terminalOutput = string.IsNullOrWhiteSpace(output) ? "Ïðîãðàììà âûïîëíåíà (âûâîäà íåò)" : output });
   }
   catch (Exception ex)
   {
       // Ïåðåõâàòûâàåì îøèáêè ñåòè (íàïðèìåð, åñëè ïðîïàë èíòåðíåò)
       return Results.Ok(new { terminalOutput = $"Îøèáêà ñåòè ïðè âûçîâå êîìïèëÿòîðà: {ex.Message}" });
   }
});

// ÐÅÃÈÑÒÐÀÖÈß
app.MapPost("/api/auth/register", async (AuthRequest request, AppDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        return Results.BadRequest(new { message = "Ëîãèí è ïàðîëü íå ìîãóò áûòü ïóñòûìè" });

    if (await db.Users.AnyAsync(u => u.Username == request.Username))
        return Results.Conflict(new { message = "Ïîëüçîâàòåëü ñ òàêèì èìåíåì óæå ñóùåñòâóåò" });

    var user = new User
    {
        Username = request.Username,
        Password= request.Password
    };

    db.Users.Add(user);
    await db.SaveChangesAsync();

    return Results.Ok(new { message = "Ðåãèñòðàöèÿ óñïåøíà" });
});

// ËÎÃÈÍ (îáíîâëåííûé)
app.MapPost("/api/auth/login", async (AuthRequest request, AppDbContext db, ILogger<Program> logger) =>
{
    logger.LogInformation("Ïîïûòêà âõîäà: {Username}", request.Username);
    var user = await db.Users.FirstOrDefaultAsync(u => u.Username == request.Username);

    if (user == null || user.Password != request.Password)
        return Results.Unauthorized(); // 401 Unauthorized

    // Êëàäåì â òîêåí Id ïîëüçîâàòåëÿ è åãî èìÿ
    var claims = new[] {
        new Claim(ClaimTypes.Name, user.Username),
        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())
    };

    var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);
    var token = new JwtSecurityToken(claims: claims, expires: DateTime.Now.AddHours(24), signingCredentials: credentials);
    var tokenString = new JwtSecurityTokenHandler().WriteToken(token);

    return Results.Ok(new { token = tokenString, username = user.Username, userId = user.Id });
});

// ÑÎÇÄÀÍÈÅ ÊÎÌÍÀÒÛ (Îáíîâëåíî: ïàðîëü òåïåðü îáÿçàòåëåí)
app.MapPost("/api/projects/create", async (Project data, AppDbContext db, ClaimsPrincipal user) =>
{
    var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    // 1. Ïðîâåðêà íà ïóñòîå èìÿ (õîðîøàÿ ïðàêòèêà)
    if (string.IsNullOrWhiteSpace(data.Name))
        return Results.BadRequest(new { message = "Íàçâàíèå êîìíàòû íå ìîæåò áûòü ïóñòûì" });

    // 2. ÍÎÂÀß ÏÐÎÂÅÐÊÀ: Ïàðîëü îáÿçàòåëåí!
    if (string.IsNullOrWhiteSpace(data.Password))
        return Results.BadRequest(new { message = "Ïàðîëü îáÿçàòåëåí äëÿ ñîçäàíèÿ êîìíàòû!" });

    // 3. Ïðîâåðêà íà óíèêàëüíîñòü èìåíè (êîòîðóþ ìû äîáàâèëè ðàíåå)
    if (await db.Projects.AnyAsync(p => p.Name == data.Name))
        return Results.Conflict(new { message = "Êîìíàòà ñ òàêèì íàçâàíèåì óæå ñóùåñòâóåò" });

    var newProject = new Project
    {
        Name = data.Name,
        Password = data.Password,
        OwnerId = userId,
        CurrentCode = "// Happy Coding!"
    };

    db.Projects.Add(newProject);
    await db.SaveChangesAsync();

    return Results.Ok(new { projectId = newProject.Id, message = "Ïðîåêò ñîçäàí" });
}).RequireAuthorization();

// ÂÕÎÄ Â ÊÎÌÍÀÒÓ (Ïðîâåðêà ïàðîëÿ ïåðåä ïîäêëþ÷åíèåì ê ñîêåòàì)
app.MapPost("/api/projects/{id}/join", async (int id, JoinProjectRequest request, AppDbContext db, ClaimsPrincipal user) =>
{
    var project = await db.Projects.FindAsync(id);
    if (project == null) return Results.NotFound(new { message = "Ïðîåêò íå íàéäåí" });

    // Ïðîâåðÿåì ïàðîëü (åñëè ïðîåêò ñ ïàðîëåì)
    if (!string.IsNullOrEmpty(project.Password) && project.Password != request.Password)
    {
        // Ðàçðåøàåì âîéòè áåç ïàðîëÿ, åñëè ýòî ñîçäàòåëü êîìíàòû
        var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (project.OwnerId != userId)
        {
            return Results.Unauthorized(); // Ïàðîëü íåâåðíûé
        }
    }

    // Åñëè âñ¸ îê, âîçâðàùàåì òåêóùèé êîä ïðîåêòà, ÷òîáû ôðîíòåíä ñðàçó åãî çàãðóçèë
    return Results.Ok(new
    {
        message = "Äîñòóï ðàçðåøåí",
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
    return Results.Ok(new { message = "Ïðîåêò óäàëåí" });
}).RequireAuthorization();

app.Run();
