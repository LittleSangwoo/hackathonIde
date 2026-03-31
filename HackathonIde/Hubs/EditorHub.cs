using Microsoft.AspNetCore.SignalR;
using System.Text.RegularExpressions;

namespace HackathonIde.Hubs
{
    public class EditorHub : Hub
    {
        // Подключение к комнате конкретного проекта
        public async Task JoinProjectSession(string projectId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, projectId);

            // Опционально: сообщаем пользователю, что он успешно вошел
            await Clients.Caller.SendAsync("SessionJoined", projectId);
        }

        // Рассылка изменений кода
        public async Task BroadcastCodeChange(string projectId, string newCode)
        {
            // Clients.OthersInGroup рассылает всем в комнате, КРОМЕ того, кто отправил код.
            // Это предотвратит бесконечный цикл обновления в редакторе (echo effect).
            await Clients.OthersInGroup(projectId).SendAsync("ReceiveCodeUpdate", newCode);
        }
    }
}
