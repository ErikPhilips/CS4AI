using System.Text.Json.Serialization;

namespace ErikPhilips.Cs4Ai;

/// <summary>
/// The structured payload returned with exit 6 (`CodeNeedsConfig`). The agent reads this,
/// surfaces the prompts to the user, then runs the `commitCommand` with the user's answer.
/// Mirrors the example in the design doc, "Initial Configuration" section.
/// </summary>
public sealed class NeedsConfigPayload
{
    [JsonPropertyName("result")]      public string Result        { get; set; } = "needs_config";
    [JsonPropertyName("reason")]      public string Reason        { get; set; } = "";
    [JsonPropertyName("repoRoot")]    public string RepoRoot      { get; set; } = "";
    [JsonPropertyName("prompts")]     public List<Prompt> Prompts { get; set; } = [];
    [JsonPropertyName("commitCommand")] public string CommitCommand { get; set; } = "cs4ai init <slnx> <preset>";
    [JsonPropertyName("afterCommit")]   public string AfterCommit   { get; set; } = "retry your original command";

    public sealed class Prompt
    {
        [JsonPropertyName("key")]      public string Key      { get; set; } = "";
        [JsonPropertyName("question")] public string Question { get; set; } = "";
        [JsonPropertyName("choices")]  public List<Choice> Choices { get; set; } = [];
    }

    public sealed class Choice
    {
        [JsonPropertyName("label")] public string Label { get; set; } = "";
        [JsonPropertyName("value")] public string Value { get; set; } = "";
    }

    public static NeedsConfigPayload For(string repoRoot, string? slnxPath = null)
    {
        var choices = Cs4AiConfig.Presets
            .Select(p => new Choice { Label = $"{p.name} — {p.description}", Value = p.name })
            .ToList();

        return new NeedsConfigPayload
        {
            Reason   = "no .cs4aiconfig at repo root",
            RepoRoot = repoRoot,
            CommitCommand = slnxPath is null
                ? "cs4ai init <slnx> <preset>"
                : $"cs4ai init {slnxPath} <preset>",
            Prompts  =
            [
                new Prompt
                {
                    Key      = "preset",
                    Question = "Pick a member-ordering style for this codebase",
                    Choices  = choices,
                },
            ],
        };
    }

    /// <summary>Plain-text rendering (cs4ai's one result format is framed text, never JSON): the
    /// reason, the preset choices, and the recovery command.</summary>
    public string ToText()
    {
        var lines = new List<string>
        {
            $"needs-config: {Reason}",
            $"repo-root: {RepoRoot}",
        };
        foreach (var p in Prompts)
        {
            lines.Add($"{p.Question}:");
            foreach (var c in p.Choices) lines.Add($"  {c.Label}");
        }
        lines.Add($"recovery: {CommitCommand}  (then {AfterCommit})");
        return string.Join("\n", lines) + "\n";
    }
}
