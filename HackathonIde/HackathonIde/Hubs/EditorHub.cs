using HackathonIde.Data;
using HackathonIde.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

namespace HackathonIde.Hubs
{
    [Authorize]
    public class EditorHub : Hub
    {
        // Словари для хранения состояния комнат в оперативной памяти (для хакатона - идеально)
        private static readonly ConcurrentDictionary<string, List<ActiveUser>> _roomUsers = new();
        private static readonly ConcurrentDictionary<string, RoomState> _roomStates = new();

        private readonly GigaChatService _aiService;
        private readonly AppDbContext _dbContext; // ДОБАВИЛИ БД
        private readonly TelegramBotService _tgBot;

        // НОВЫЙ КОД: Открываем статистику для бота
        public static int ActiveRoomsCount => _roomUsers.Count;
        public static int ActiveUsersCount => _roomUsers.Values.Sum(v => v.Count);
        // Внедряем сервисы через конструктор
        public EditorHub(GigaChatService aiService, AppDbContext dbContext, TelegramBotService tgBot)
        {
            _aiService = aiService;
            _dbContext = dbContext;
            _tgBot = tgBot;
        }

        // ВХОД В КОМНАТУ (Решает задачи 6 и 12)
        public async Task JoinProjectSession(string projectId)
        {
            var userName = Context.User?.Identity?.Name ?? "Anonymous";
            var connectionId = Context.ConnectionId;

            // Генерация простой аватарки по имени пользователя (Task 12)
            var avatarUrl = $"https://ui-avatars.com/api/?name={userName}&background=random";
            var user = new ActiveUser { ConnectionId = connectionId, Username = userName, AvatarUrl = avatarUrl };

            // Добавляем юзера в комнату
            _roomUsers.AddOrUpdate(projectId,
                _ => new List<ActiveUser> { user },
                (_, list) => {
                    if (!list.Any(u => u.ConnectionId == connectionId)) list.Add(user);
                    return list;
                });

            await Groups.AddToGroupAsync(connectionId, projectId);

            // Уведомление в ленту (Task 5, 13)
            await Clients.Group(projectId).SendAsync("ReceiveSystemEvent", $"{userName} подключился к сессии");

            // Обновление списка участников для всех (Task 6)
            await Clients.Group(projectId).SendAsync("UpdateUserList", _roomUsers[projectId]);

            // --- НОВЫЙ КОД ДЛЯ СИНХРОНИЗАЦИИ ---
            string currentCode = "// Напишите ваш C# код здесь...";

            // 1. Сначала ищем самую свежую версию в оперативной памяти (из Undo/Redo)
            if (_roomStates.TryGetValue(projectId, out var state) && state.History.Any())
            {
                currentCode = state.History[state.CurrentIndex];
            }
            // 2. Если в памяти пусто (сервер перезапускался), достаем из БД
            else if (int.TryParse(projectId, out int id))
            {
                var project = await _dbContext.Projects.FindAsync(id);
                if (project != null && !string.IsNullOrWhiteSpace(project.CurrentCode))
                {
                    currentCode = project.CurrentCode;
                }
            }

            // 3. Отправляем код ТОЛЬКО тому, кто зашел (Caller), чтобы его редактор заполнился
            await Clients.Caller.SendAsync("ReceiveCodeUpdate", currentCode);
        }

        // ВЫХОД ИЗ КОМНАТЫ ПРИ ОТКЛЮЧЕНИИ (Решает задачу 2 и 6)
        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            var connectionId = Context.ConnectionId;

            foreach (var room in _roomUsers)
            {
                var user = room.Value.FirstOrDefault(u => u.ConnectionId == connectionId);
                if (user != null)
                {
                    room.Value.Remove(user); // Удаляем из списка
                    await Clients.Group(room.Key).SendAsync("ReceiveSystemEvent", $"{user.Username} покинул сессию");
                    await Clients.Group(room.Key).SendAsync("UpdateUserList", room.Value); // Обновляем сайдбар у остальных
                }
            }

            await base.OnDisconnectedAsync(exception);
        }

        // РАССЫЛКА И СОХРАНЕНИЕ КОДА (Для Task 8 - Undo/Redo)
        public async Task BroadcastCodeChange(string projectId, object changeData, string newCode)
        {
            // --- НОВЫЙ КОД ДЛЯ СОХРАНЕНИЯ ПРОЕКТА ---
            if (int.TryParse(projectId, out int id))
            {
                var project = await _dbContext.Projects.FindAsync(id);
                if (project != null)
                {
                    project.CurrentCode = newCode;
                    await _dbContext.SaveChangesAsync(); // Фиксируем в базе!
                }
            }
            // Просто пересылаем данные остальным (для хакатона это самый быстрый фикс)
            await Clients.OthersInGroup(projectId).SendAsync("ReceiveCodeUpdate", changeData);
        }

        // Метод для рассылки вывода терминала всем участникам проекта
        public async Task SendTerminalOutput(string projectId, string output)
        {
            // Отправляем ВСЕМ в группе (включая отправителя, чтобы у всех был один лог)
            await Clients.Group(projectId).SendAsync("ReceiveTerminalOutput", output);
        }

        // Task 8: КНОПКИ UNDO / REDO
        public async Task RequestUndo(string projectId)
        {
            if (_roomStates.TryGetValue(projectId, out var state) && state.CurrentIndex > 0)
            {
                state.CurrentIndex--;
                var previousCode = state.History[state.CurrentIndex];
                // Отправляем всем, включая того, кто нажал Undo, чтобы код синхронизировался
                await Clients.Group(projectId).SendAsync("ReceiveCodeUpdate", previousCode);
                await Clients.Group(projectId).SendAsync("ReceiveSystemEvent", $"{Context.User?.Identity?.Name} отменил изменение");
            }
        }

        public async Task RequestRedo(string projectId)
        {
            if (_roomStates.TryGetValue(projectId, out var state) && state.CurrentIndex < state.History.Count - 1)
            {
                state.CurrentIndex++;
                var nextCode = state.History[state.CurrentIndex];
                await Clients.Group(projectId).SendAsync("ReceiveCodeUpdate", nextCode);
                await Clients.Group(projectId).SendAsync("ReceiveSystemEvent", $"{Context.User?.Identity?.Name} вернул изменение");
            }
        }

        // Task 10 & 11: AI РАЗРЕШЕНИЕ КОНФЛИКТОВ
        public async Task TriggerAiConflictResolution(string projectId, string codeA, string codeB)
        {
            await Clients.Group(projectId).SendAsync("ReceiveSystemEvent", "🤖 AI анализирует конфликт кода...");

            try
            {
                var resolvedCode = await _aiService.ResolveConflictAsync(codeA, codeB);
                // Отправляем решение на фронт (там можно показать красивую модалку "Принять / Отклонить")
                await Clients.Group(projectId).SendAsync("ReceiveAiResolution", resolvedCode);
                await Clients.Group(projectId).SendAsync("ReceiveSystemEvent", "🤖 AI предложил решение слияния!");
                _ = _tgBot.SendLogAsync($"🤖 [Комната {projectId}] Сработал AI-ревьюер для разрешения конфликта!");
            }
            catch (Exception ex)
            {
                await Clients.Group(projectId).SendAsync("ReceiveSystemEvent", $"Ошибка AI: {ex.Message}");
            }
        }

        // Task 7: Курсоры (уже было, оставляем)
        public async Task BroadcastCursor(string projectId, string user, int lineNumber, int column)
        {
            await Clients.OthersInGroup(projectId).SendAsync("ReceiveCursor", user, lineNumber, column);
        }
    }

    // Вспомогательные классы для хранения состояния
    public class ActiveUser
    {
        public string ConnectionId { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string AvatarUrl { get; set; } = string.Empty;
    }

    public class RoomState
    {
        public List<string> History { get; set; } = new();
        public int CurrentIndex { get; set; } = -1;
    }
}