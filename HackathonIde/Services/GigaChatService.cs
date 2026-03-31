using System.Text;
using System.Text.Json;

namespace HackathonIde.Services
{
    public class GigaChatService
    {
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;
        private string? _accessToken;
        private DateTime _tokenExpiry;

        public GigaChatService(HttpClient httpClient, IConfiguration configuration)
        {
            _httpClient = httpClient;
            _configuration = configuration;
        }

        // 1. Получение токена доступа
        private async Task<string> GetTokenAsync()
        {
            // Если токен еще жив, используем его
            if (!string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _tokenExpiry)
                return _accessToken;

            var authKey = _configuration["GigaChat:AuthKey"];
            var request = new HttpRequestMessage(HttpMethod.Post, "https://ngw.devices.sberbank.ru:9443/api/v2/oauth");

            request.Headers.Add("Authorization", $"Bearer {authKey}");
            request.Headers.Add("RqUID", Guid.NewGuid().ToString());
            request.Content = new StringContent("scope=GIGACHAT_API_PERS", Encoding.UTF8, "application/x-www-form-urlencoded");

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            _accessToken = doc.RootElement.GetProperty("access_token").GetString();
            // Токен живет 30 минут, берем с запасом 28
            _tokenExpiry = DateTime.UtcNow.AddMinutes(28);

            return _accessToken!;
        }

        // 2. Отправка кода на ревью
        public async Task<string> GetCodeReviewAsync(string code)
        {
            var token = await GetTokenAsync();
            var request = new HttpRequestMessage(HttpMethod.Post, "https://gigachat.devices.sberbank.ru/api/v1/chat/completions");

            request.Headers.Add("Authorization", $"Bearer {token}");

            // Формируем промпт для нейросети
            var payload = new
            {
                model = "GigaChat",
                messages = new[]
                {
                new { role = "system", content = "Ты опытный Senior C# разработчик. Твоя задача — проводить code-review. Укажи на ошибки, антипаттерны и предложи улучшения. Отвечай кратко и по делу." },
                new { role = "user", content = $"Проверь этот код:\n\n{code}" }
            },
                temperature = 0.7
            };

            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "Нет ответа";
        }
    }
}
