using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace PrivChat.Overlay;

internal sealed record AiTurn(string Role, string Text, byte[]? Image = null);

internal static class GeminiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<string> AskAsync(
        HttpClient http,
        AiConfig config,
        IReadOnlyList<AiTurn> history,
        string prompt,
        byte[]? imageJpeg)
    {
        if (!IsSafeModelName(config.Model))
        {
            throw new InvalidOperationException($"Invalid model name \"{config.Model}\".");
        }

        // Base64 + JSON of a screenshot is expensive; keep it off the UI thread.
        var requestJson = await Task.Run(() => BuildRequestJson(history, prompt, imageJpeg)).ConfigureAwait(false);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://generativelanguage.googleapis.com/v1beta/models/{config.Model}:generateContent");
        // The header keeps the key out of the URL.
        request.Headers.TryAddWithoutValidation("x-goog-api-key", config.ApiKey);
        request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(request).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var detail = JsonSerializer.Deserialize<ErrorEnvelope>(json, JsonOptions)?.Error?.Message;
            throw new InvalidOperationException(
                detail is null ? $"Gemini error {(int)response.StatusCode} {response.StatusCode}." : $"Gemini error {(int)response.StatusCode}: {detail}");
        }

        var gemini = JsonSerializer.Deserialize<GenerateContentResponse>(json, JsonOptions);
        var text = string.Join(string.Empty, gemini?.Candidates?.FirstOrDefault()?.Content?.Parts?
            .Select(part => part.Text)
            .Where(t => !string.IsNullOrEmpty(t)) ?? Enumerable.Empty<string?>());
        return text.Length > 0
            ? text
            : "Gemini returned no text (the response may have been blocked by safety filters).";
    }

    private static string BuildRequestJson(IReadOnlyList<AiTurn> history, string prompt, byte[]? imageJpeg)
    {
        var contents = new List<object>(history.Count + 1);
        foreach (var turn in history)
        {
            contents.Add(new { role = turn.Role, parts = BuildParts(turn.Text, turn.Image) });
        }

        contents.Add(new { role = "user", parts = BuildParts(prompt, imageJpeg) });
        return JsonSerializer.Serialize(new { contents }, JsonOptions);
    }

    private static object[] BuildParts(string text, byte[]? image)
    {
        var parts = new List<object> { new { text } };
        if (image is { Length: > 0 })
        {
            parts.Add(new { inlineData = new { mimeType = ScreenGrabber.JpegMimeType, data = Convert.ToBase64String(image) } });
        }

        return parts.ToArray();
    }

    private static bool IsSafeModelName(string model) =>
        model.Length > 0 && model.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    private sealed record ErrorEnvelope(ErrorBody? Error);
    private sealed record ErrorBody(string? Message);
    private sealed record GenerateContentResponse(Candidate[]? Candidates);
    private sealed record Candidate(ContentBody? Content);
    private sealed record ContentBody(PartBody[]? Parts);
    private sealed record PartBody(string? Text);
}
