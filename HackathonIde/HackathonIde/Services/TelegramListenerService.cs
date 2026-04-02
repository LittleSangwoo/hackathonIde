using HackathonIde.Hubs;
using Microsoft.AspNetCore.SignalR;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types;

namespace HackathonIde.Services
{
    public class TelegramListenerService : BackgroundService
    {
        private readonly IHubContext<EditorHub> _hubContext;
        // ВАЖНО: Вставь сюда токен твоего бота от BotFather!
        private readonly string _botToken = "8704409981:AAE4s8d1JdEM8XNOX9au1cg0_9q7ebIsrt4";

        public TelegramListenerService(IHubContext<EditorHub> hubContext)
        {
            _hubContext = hubContext;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var botClient = new TelegramBotClient(_botToken);
            var receiverOptions = new ReceiverOptions { AllowedUpdates = Array.Empty<UpdateType>() };

            botClient.StartReceiving(HandleUpdateAsync, HandleErrorAsync, receiverOptions, stoppingToken);

            // Держим сервис запущенным вечно
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(1000, stoppingToken);
            }
        }

        private async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
        {
            // Нас интересуют только текстовые сообщения
            if (update.Type != UpdateType.Message || update.Message!.Text == null) return;

            var text = update.Message.Text;
            var chatId = update.Message.Chat.Id;

            try
            {
                if (text.StartsWith("/stats"))
                {
                    int rooms = EditorHub.ActiveRoomsCount;
                    int users = EditorHub.ActiveUsersCount;
                    await botClient.SendTextMessageAsync(chatId,
                        $"📊 <b>Статистика OnliSharp IDE:</b>\nАктивных комнат: {rooms}\nПользователей онлайн: {users}",
                        parseMode: ParseMode.Html,
                        cancellationToken: cancellationToken);
                }
                else if (text.StartsWith("/broadcast "))
                {
                    var msg = text.Replace("/broadcast ", "");

                    // 🔥 МАГИЯ: Отправляем сообщение из ТГ прямиком в веб-сокеты всем юзерам!
                    await _hubContext.Clients.All.SendAsync("ReceiveSystemEvent", $"📢 [АДМИН из ТГ]: {msg}", cancellationToken: cancellationToken);

                    await botClient.SendTextMessageAsync(chatId, "✅ Системное уведомление успешно отправлено всем пользователям IDE!", cancellationToken: cancellationToken);
                }
                else if (text.StartsWith("/invite "))
                {
                    var roomId = text.Replace("/invite ", "").Trim();

                    // ID вашей публичной группы (я взял его из твоего TelegramBotService.cs)
                    var groupId = "-5191144557";

                    // Формируем красивое сообщение. Тег <code> делает текст копируемым по клику!
                    var inviteMsg = $"🚀 <b>Внимание, программисты!</b>\n\nОткрыта новая сессия для совместного кодинга!\n\n🔑 <b>ID комнаты:</b> <code>{roomId}</code>\n<i>(Нажмите на ID, чтобы скопировать)</i>\n\n🌐 <b>Заходите на платформу:</b>\nhttps://tova-seminivorous-lavona.ngrok-free.dev";

                    // 1. Отправляем приглашение в ВАШУ ОБЩУЮ ГРУППУ
                    await botClient.SendTextMessageAsync(groupId, inviteMsg, parseMode: ParseMode.Html, cancellationToken: cancellationToken);

                    // 2. Отвечаем админу в личку, что всё прошло успешно
                    await botClient.SendTextMessageAsync(chatId, $"✅ Приглашение в комнату {roomId} успешно отправлено в общую группу!", cancellationToken: cancellationToken);
                }
                else
                {
                    await botClient.SendTextMessageAsync(chatId, "<b>Доступные команды управления:</b>\n/stats - посмотреть онлайн\n/broadcast [текст] - глобальное пуш-уведомление всем юзерам", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка Telegram Bot: {ex.Message}");
            }
        }

        private Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
