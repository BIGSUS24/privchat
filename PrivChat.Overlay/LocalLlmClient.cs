using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace PrivChat.Overlay;

// Text-only history: the local model has a small context and every image costs
// hundreds of tokens, so only the turn being sent ever carries an image.
internal sealed record LlmTurn(string Role, string Text);

internal static class LocalLlmClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<string> AskAsync(
        HttpClient http,
        string endpoint,
        IReadOnlyList<LlmTurn> history,
        string prompt,
        byte[]? imageJpeg)
    {
        var requestJson = await Task.Run(() => BuildRequestJson(history, prompt, imageJpeg)).ConfigureAwait(false);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{endpoint.TrimEnd('/')}/v1/chat/completions");
        // llama-server needs no API key.
        request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

        string json;
        try
        {
            using var response = await http.SendAsync(request).ConfigureAwait(false);
            json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var detail = JsonSerializer.Deserialize<ErrorEnvelope>(json, JsonOptions)?.Error?.Message;
                throw new InvalidOperationException(
                    detail is null
                        ? $"Local LLM error {(int)response.StatusCode} {response.StatusCode}."
                        : $"Local LLM error {(int)response.StatusCode}: {detail}");
            }
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException(
                $"Could not reach the local LLM at {endpoint}. Start it with the START HERE.bat and try again.");
        }

        var chat = JsonSerializer.Deserialize<ChatCompletionResponse>(json, JsonOptions);
        var text = chat?.Choices?.FirstOrDefault()?.Message?.Content ?? string.Empty;
        return text.Length > 0
            ? text
            : "The local model returned no text (the request may have exceeded its context window).";
    }

    private static string BuildRequestJson(IReadOnlyList<LlmTurn> history, string prompt, byte[]? image)
    {
        var messages = new List<object>(history.Count + 1);
        foreach (var turn in history)
        {
            // History stores "model"; the chat/completions API wants "assistant".
            var role = turn.Role == "model" ? "assistant" : turn.Role;
            messages.Add(new { role, content = turn.Text });
        }

        messages.Add(new { role = "user", content = BuildContent(prompt, image) });
        return JsonSerializer.Serialize(new { messages }, JsonOptions);
    }

    private static object BuildContent(string text, byte[]? image)
    {
        if (image is not { Length: > 0 })
        {
            return text;
        }

        // Image part first, text second — the order the local Qwen3-VL setup sends.
        return new object[]
        {
            new { type = "image_url", image_url = new { url = $"data:{ScreenGrabber.JpegMimeType};base64,{Convert.ToBase64String(image)}" } },
            new { type = "text", text }
        };
    }

    private sealed record ErrorEnvelope(ErrorBody? Error);
    private sealed record ErrorBody(string? Message);
    private sealed record ChatCompletionResponse(Choice[]? Choices);
    private sealed record Choice(MessageBody? Message);
    private sealed record MessageBody(string? Content);
}
