using System.Text.Json;
using System.Diagnostics.CodeAnalysis;

static class NeutrinoPaths
{
    public static string Home => Environment.GetEnvironmentVariable("NEUTRINO_HOME") is { Length: > 0 } home
        ? home : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".neutrino");
}

// Only return the display and selection fields. model.json can contain API keys
// and endpoint URLs, neither of which belongs in the browser response or logs.
static class NeutrinoModels
{
    static readonly string[] Levels = ["Low", "Medium", "High", "Max", "Ultra"];
    public record Model(string Name, bool Configured, string[] Intensities);
    public record Provider(string Name, Model[] Models);
    public record Selection(string Provider, string Model, string Intensity);
    public record Catalog(Provider[] Providers, string[] Intensities, Selection Selection);

    public static Catalog Read(string home)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(home, "model.json")));
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("providers", out var entries) || entries.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("模型文件格式不正确。");
        var providers = new List<Provider>();
        foreach (var item in entries.EnumerateArray()) {
            if (item.ValueKind != JsonValueKind.Object || !TryString(item, "name", out var providerName) || !item.TryGetProperty("models", out var modelList) || modelList.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("模型文件格式不正确。");
            var models = new List<Model>();
            foreach (var model in modelList.EnumerateArray()) {
                if (model.ValueKind != JsonValueKind.Object || !TryString(model, "name", out var name))
                    throw new InvalidDataException("模型文件格式不正确。");
                var configured = TryString(model, "api_key", out var key) && !string.IsNullOrWhiteSpace(key);
                var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (model.TryGetProperty("intensity", out var raw)) {
                    if (raw.ValueKind == JsonValueKind.String) AddIntensity(supported, raw);
                    else if (raw.ValueKind == JsonValueKind.Array) foreach (var level in raw.EnumerateArray()) AddIntensity(supported, level);
                }
                var levels = supported.Count == 0 ? Levels.ToArray() : Levels.Where(supported.Contains).ToArray();
                models.Add(new Model(name, configured, levels));
            }
            providers.Add(new Provider(providerName, models.ToArray()));
        }

        string? selectedProvider = null, selectedModel = null, selectedIntensity = null, defaultIntensity = null;
        try {
            using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(home, "config.json")));
            if (config.RootElement.ValueKind == JsonValueKind.Object) {
                TryString(config.RootElement, "selected_provider", out selectedProvider);
                TryString(config.RootElement, "selected_model", out selectedModel);
                TryString(config.RootElement, "selected_intensity", out selectedIntensity);
                TryString(config.RootElement, "default_intensity", out defaultIntensity);
            }
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        var provider = providers.FirstOrDefault(p => p.Name == selectedProvider)
            ?? providers.FirstOrDefault(p => p.Models.Any(m => m.Configured)) ?? providers.FirstOrDefault();
        var chosen = provider?.Models.FirstOrDefault(m => m.Name == selectedModel)
            ?? provider?.Models.FirstOrDefault(m => m.Configured) ?? provider?.Models.FirstOrDefault();
        var available = chosen?.Intensities ?? Levels;
        var preferred = selectedIntensity ?? defaultIntensity;
        var intensity = new[] { preferred, defaultIntensity, "High" }
            .Select(CanonicalIntensity).FirstOrDefault(level => level is not null && available.Contains(level)) ?? available.FirstOrDefault() ?? "High";
        return new Catalog(providers.ToArray(), Levels.ToArray(), new Selection(provider?.Name ?? "", chosen?.Name ?? "", intensity));
    }
    static bool TryString(JsonElement parent, string property, [NotNullWhen(true)] out string? value) {
        value = null;
        if (!parent.TryGetProperty(property, out var entry) || entry.ValueKind != JsonValueKind.String) return false;
        value = entry.GetString(); return value is not null;
    }
    static string? CanonicalIntensity(string? value) => Levels.FirstOrDefault(level => string.Equals(level, value?.Trim(), StringComparison.OrdinalIgnoreCase));
    static void AddIntensity(HashSet<string> supported, JsonElement entry) {
        if (entry.ValueKind == JsonValueKind.String && CanonicalIntensity(entry.GetString()) is { } level) supported.Add(level);
    }
}
