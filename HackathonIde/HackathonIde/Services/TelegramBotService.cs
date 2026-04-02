namespace HackathonIde.Services
{
    public class TelegramBotService
    {
        private readonly HttpClient _httpClient;
        // Вставь свои данные сюда (или вынеси в appsettings.json, если есть время)
        private readonly string _botToken = "8704409981:AAE4s8d1JdEM8XNOX9au1cg0_9q7ebIsrt4";
        private readonly string _chatId = "-5191144557";

        public TelegramBotService(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public async Task SendLogAsync(string message)
        {
            try
            {
                var url = $"https://api.telegram.org/bot{_botToken}/sendMessage?chat_id={_chatId}&text={Uri.EscapeDataString(message)}";
                await _httpClient.GetAsync(url);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка отправки в ТГ: {ex.Message}");
            }
        }
    }
}
