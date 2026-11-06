using ConsoleApp;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

internal class Program
{
    private const string Scope = "GIGACHAT_API_PERS";

    private const string Model = "GigaChat-2";

    private const int MaxToken = 1000;

    private const bool ToJson = true;

    private static readonly object ResponseFormat = new
    {
        type = "json_schema",
        schema = new
        {
            type = "object",
            properties = new
            {
                name = new { type = "string" },
                year = new { type = "integer" },
                purpose = new { type = "string" },
                advantages = new { type = "array", items = new { type = "string" } },
                disadvantages = new { type = "array", items = new { type = "string" } }
            },
            required = new[] { "name", "year", "purpose", "advantages", "disadvantages" },
            additionalProperties = false
        },
        strict = true
    };


    private static async Task Main()
    {
        string? authKey = Environment.GetEnvironmentVariable("GIGACHAT_AUTH_KEY");

        if (string.IsNullOrWhiteSpace(authKey))
        {
            Console.WriteLine("Нет переменной окружения GIGACHAT_AUTH_KEY (Authorization key).");
            return;
        }

        using HttpClientHandler handler = new()
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };

        using HttpClient http = new(handler);

        // Получаем токен один раз на запуск (действует ~30 минут)
        string accessToken = await GetAccessTokenAsync(http, authKey);

        List<object> history = [
            new { role = "system", content = "Ты полезный ассистент. Отвечай не сильно много" }
        ];

        Console.WriteLine("Чат запущен. Напиши сообщение. Команды: /exit, /reset");

        while (true)
        {
            Console.Write("> ");
            string? input = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(input))
            {
                continue;
            }

            if (input.Equals("/exit", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (input.Equals("/reset", StringComparison.OrdinalIgnoreCase))
            {
                history.Clear();
                history.Add(new { role = "system", content = "Ты полезный ассистент. Отвечай не сильно много." });
                Console.WriteLine("История очищена.");
                continue;
            }

            history.Add(new { role = "user", content = input });

            try
            {
                await Output(http, accessToken, history, MaxToken, ToJson);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                // Токен мог протухнуть — получим заново и повторим 1 раз
                accessToken = await GetAccessTokenAsync(http, authKey);

                await Output(http, accessToken, history, MaxToken, ToJson);
            }
        }
    }

    private static async Task Output(HttpClient http, string accessToken, List<object> history, int? maxTokens = null, bool toJson = false)
    {
        object? responseFormat = null;

        if (toJson)
        {
            responseFormat = ResponseFormat;
        }

        ChatResult result = await ChatAsync(http, accessToken, history, maxTokens, responseFormat);

        Console.WriteLine("\nОтвет модели:");
        Console.WriteLine(result.Content);

        Console.WriteLine("\n--- Статистика запроса ---");
        Console.WriteLine($"Finish reason: {result.FinishReason}");
        Console.WriteLine($"Prompt tokens: {result.PromptTokens}");
        Console.WriteLine($"Completion tokens: {result.CompletionTokens}");
        Console.WriteLine($"Total tokens: {result.TotalTokens}");

        history.Add(new { role = "assistant", content = result.Content });

        if (!toJson)
        {
            return;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(result.Content);
            Console.WriteLine("\nJSON успешно разобран!");
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"\nОшибка JSON: {ex.Message}");
        }

        try
        {
            LanguageInfo? language = JsonSerializer.Deserialize<LanguageInfo>(
                result.Content,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

            if (language == null)
            {
                Console.WriteLine("\nНе удалось получить объект.");
                return;
            }

            Console.WriteLine($"\nЯзык: {language.Name}");
            Console.WriteLine($"Год: {language.Year}");
            Console.WriteLine($"Назначение: {language.Purpose}");

            Console.WriteLine("Преимущества:");
            foreach (string item in language.Advantages)
            {
                Console.WriteLine($"- {item}");
            }

            Console.WriteLine("Недостатки:");
            foreach (string item in language.Disadvantages)
            {
                Console.WriteLine($"- {item}");
            }
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"\nОшибка десериализации: {ex.Message}");
        }



    }

    private static async Task<string> GetAccessTokenAsync(HttpClient http, string authKey)
    {
        using HttpRequestMessage req = new(HttpMethod.Post, "https://ngw.devices.sberbank.ru:9443/api/v2/oauth");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Add("RqUID", Guid.NewGuid().ToString());
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", authKey);

        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["scope"] = Scope
        });

        HttpResponseMessage resp = await http.SendAsync(req);
        string text = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
        {
            throw new Exception($"Не удалось получить access token. HTTP {(int)resp.StatusCode}\n{text}");
        }

        using JsonDocument doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("access_token").GetString()
               ?? throw new Exception("В ответе нет access_token");
    }

    private static async Task<ChatResult> ChatAsync(HttpClient http, string accessToken, List<object> messages, int? maxTokens = null, object? responseFormat = null)
    {
        var body = new
        {
            model = Model,
            messages,
            max_tokens = maxTokens,
            response_format = responseFormat,
            stop = new[] { "<END>" }
        };

        JsonSerializerOptions options = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        string json = JsonSerializer.Serialize(body, options);

        using HttpRequestMessage request = new(
            HttpMethod.Post,
            "https://api.giga.chat/v1/chat/completions");

        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", accessToken);

        request.Content = new StringContent(
            json,
            Encoding.UTF8,
            "application/json");

        using HttpResponseMessage response = await http.SendAsync(request);

        string responseText = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();

        using JsonDocument document = JsonDocument.Parse(responseText);

        JsonElement root = document.RootElement;
        JsonElement choice = root.GetProperty("choices")[0];
        JsonElement message = choice.GetProperty("message");
        JsonElement usage = root.GetProperty("usage");

        return new ChatResult
        {
            Content = message.GetProperty("content").GetString() ?? "",
            FinishReason = choice.GetProperty("finish_reason").GetString() ?? "unknown",
            PromptTokens = usage.GetProperty("prompt_tokens").GetInt32(),
            CompletionTokens = usage.GetProperty("completion_tokens").GetInt32(),
            TotalTokens = usage.GetProperty("total_tokens").GetInt32()
        };
    }
}