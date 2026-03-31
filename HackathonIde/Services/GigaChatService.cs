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

            // ВАЖНО: При получении токена используется Basic
            request.Headers.Add("Authorization", $"Basic {authKey}");
            request.Headers.Add("RqUID", Guid.NewGuid().ToString());
            request.Headers.Add("Accept", "application/json");
            request.Content = new StringContent("scope=GIGACHAT_API_PERS", Encoding.UTF8, "application/x-www-form-urlencoded");

            var response = await _httpClient.SendAsync(request);

            // ВЫТАСКИВАЕМ РЕАЛЬНУЮ ПРИЧИНУ ОШИБКИ
            if (!response.IsSuccessStatusCode)
            {
                var errorText = await response.Content.ReadAsStringAsync();
                throw new Exception($"Сбер отклонил ключ (Токен): {errorText}");
            }

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

            if (!response.IsSuccessStatusCode)
            {
                var errorText = await response.Content.ReadAsStringAsync();
                throw new Exception($"Сбер отклонил промпт (AI): {errorText}");
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "Нет ответа";
        }

        public async Task<string> ResolveConflictAsync(string codeA, string codeB)
        {
            // 1. Получаем живой токен
            var token = await GetTokenAsync();

            // 2. Настраиваем запрос к самой нейросети
            var request = new HttpRequestMessage(HttpMethod.Post, "https://gigachat.devices.sberbank.ru/api/v1/chat/completions");
            request.Headers.Add("Authorization", $"Bearer {token}");

            // 3. Формируем тело запроса (Payload)
            var payload = new
            {
                model = "GigaChat",
                messages = new[]
                {
                new { role = "system", content = "Ты AI-агент для разрешения Git-конфликтов. Дано два варианта одной функции от разных программистов. Напиши итоговый, объединенный вариант без конфликтов. В ответе пришли только готовый код без лишних пояснений." },
                new { role = "user", content = $"Вариант 1:\n{codeA}\n\nВариант 2:\n{codeB}" }
            },
                temperature = 0.5 // Чуть снизили температуру, чтобы ИИ давал строгий код, а не фантазировал
            };

            // 4. Упаковываем C#-объект в JSON строку
            var jsonContent = JsonSerializer.Serialize(payload);
            request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            // 5. Отправляем запрос!
            var response = await _httpClient.SendAsync(request);

            // Если что-то упало (например, лимиты исчерпаны), выкинет ошибку
            response.EnsureSuccessStatusCode();

            // 6. Читаем и разбираем ответ от ИИ
            var responseJson = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseJson);

            // GigaChat возвращает сложный JSON. Нам нужно провалиться в choices -> [0] -> message -> content
            var resolvedCode = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            return resolvedCode ?? "Не удалось сгенерировать решение.";
        }
    }
}
