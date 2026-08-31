using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

class Program {
    const string Scope = "GIGACHAT_API_PERS";
    const string Model = "GigaChat-3-Ultra";

    static async Task Main() {
        var authKey = Environment.GetEnvironmentVariable("GIGACHAT_AUTH_KEY");
        if (string.IsNullOrWhiteSpace(authKey)) {
            Console.WriteLine("Нет переменной окружения GIGACHAT_AUTH_KEY (Authorization key).");
            return;
        }

        using var handler = new HttpClientHandler {
            // В корпсети/VPN может ломаться TLS. Для ДЗ можно оставить, но в проде убрать.
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };

        using var http = new HttpClient(handler);

        // Получаем токен один раз на запуск (действует ~30 минут)
        var accessToken = await GetAccessTokenAsync(http, authKey);

        var history = new List<object> {
            new { role = "system", content = "Ты полезный ассистент. Отвечай не сильно много" }
        };

        Console.WriteLine("Чат запущен. Напиши сообщение. Команды: /exit, /reset");

        while (true) {
            Console.Write("> ");
            var input = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(input))
                continue;

            if (input.Equals("/exit", StringComparison.OrdinalIgnoreCase))
                break;

            if (input.Equals("/reset", StringComparison.OrdinalIgnoreCase)) {
                history.Clear();
                history.Add(new { role = "system", content = "Ты полезный ассистент. Отвечай не сильно много." });
                Console.WriteLine("История очищена.");
                continue;
            }

            history.Add(new { role = "user", content = input });

            try {
                var assistant = await ChatAsync(http, accessToken, history);
                Console.WriteLine(assistant);
                history.Add(new { role = "assistant", content = assistant });
            } catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized) {
                // Токен мог протухнуть — получим заново и повторим 1 раз
                accessToken = await GetAccessTokenAsync(http, authKey);

                var assistant = await ChatAsync(http, accessToken, history);
                Console.WriteLine(assistant);
                history.Add(new { role = "assistant", content = assistant });
            }
        }
    }

    static async Task<string> GetAccessTokenAsync(HttpClient http, string authKey) {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://ngw.devices.sberbank.ru:9443/api/v2/oauth");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Add("RqUID", Guid.NewGuid().ToString());
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", authKey);

        req.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
            ["scope"] = Scope
        });

        var resp = await http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Не удалось получить access token. HTTP {(int)resp.StatusCode}\n{text}");

        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("access_token").GetString()
               ?? throw new Exception("В ответе нет access_token");
    }

    static async Task<string> ChatAsync(HttpClient http, string accessToken, List<object> messages) {
        var body = new {
            model = Model,
            messages = messages
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.giga.chat/v1/chat/completions");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        var resp = await http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode}\n{text}", null, resp.StatusCode);

        using var doc = JsonDocument.Parse(text);
        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? text;
    }
}