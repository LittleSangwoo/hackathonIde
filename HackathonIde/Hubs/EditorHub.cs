using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using System.Text.RegularExpressions;

namespace HackathonIde.Hubs
{
    [Authorize]
    public class EditorHub : Hub
    {
        // Подключение к комнате конкретного проекта
        //public async Task JoinProjectSession(string projectId)
        //{
        //    await Groups.AddToGroupAsync(Context.ConnectionId, projectId);

        //    // Опционально: сообщаем пользователю, что он успешно вошел
        //    await Clients.Caller.SendAsync("SessionJoined", projectId);
        //}

        // Рассылка изменений кода
        public async Task BroadcastCodeChange(string projectId, string newCode)
        {
            // Clients.OthersInGroup рассылает всем в комнате, КРОМЕ того, кто отправил код.
            // Это предотвратит бесконечный цикл обновления в редакторе (echo effect).
            await Clients.OthersInGroup(projectId).SendAsync("ReceiveCodeUpdate", newCode);
        }

        // Метод для передачи координат курсора (строка и столбец)
        public async Task BroadcastCursor(string projectId, string user, int lineNumber, int column)
        {
            await Clients.OthersInGroup(projectId).SendAsync("ReceiveCursor", user, lineNumber, column);
        }

        public async Task JoinProjectSession(string projectId)
        {
            var userName = Context.User?.Identity?.Name ?? "Anonymous";
            await Groups.AddToGroupAsync(Context.ConnectionId, projectId);
            // Отправляем всем сообщение для ленты событий
            await Clients.Group(projectId).SendAsync("ReceiveSystemEvent", $"{userName} подключился к сессии");
        }

        // 2. Метод для ленты событий (например, "Иван нажал Compile")
        public async Task SendSystemEvent(string projectId, string message)
        {
            await Clients.Group(projectId).SendAsync("ReceiveSystemEvent", message);
        }
    }
}
