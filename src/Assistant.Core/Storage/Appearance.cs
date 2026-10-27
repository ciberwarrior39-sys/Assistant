using System.Text.Json.Serialization;

namespace Assistant.Core.Storage;

/// <summary>
/// How the character and the chat bubble look, from the "appearance" part of config.json.
/// The Settings window edits it with a live preview; hand edits work too (restart after those).
/// The colors are the built-in characters' body colors, and the glow a custom character gets for moods it has no animation for.
/// </summary>
public sealed class Appearance
{
    /// <summary>The characters drawn by the app. Custom ones are "custom:" plus their folder name in characters\.</summary>
    public static readonly string[] BuiltInCharacters = ["blob", "robot", "cat"];

    public const string CustomPrefix = "custom:";

    /// <summary>blob, robot, cat, or custom:&lt;folder name&gt;.</summary>
    public string Character { get; set; } = "blob";

    /// <summary>The folder name of a custom character, or null for a built-in one.</summary>
    [JsonIgnore]
    public string? CustomCharacter =>
        Character.StartsWith(CustomPrefix, StringComparison.Ordinal) ? Character[CustomPrefix.Length..] : null;

    /// <summary>The built-in colors, by mood. Keys are the mood names the app uses.</summary>
    public static readonly IReadOnlyDictionary<string, string> DefaultColors = new Dictionary<string, string>
    {
        ["Idle"] = "#4A90E2",
        ["Thinking"] = "#8E6CEF",
        ["FoundIt"] = "#2EB872",
        ["Confused"] = "#F09A3E",
        ["Happy"] = "#7CC542",
        ["Tired"] = "#8A94A3",
        ["Sleepy"] = "#3F5A9E",
        ["Review"] = "#E8B33A",
        ["HandRaised"] = "#F5A623",
        ["Listening"] = "#E24A6A",
        ["Speaking"] = "#3EA8D8",
        ["TimeUp"] = "#F05A28",
    };

    /// <summary>The character's size in percent: any whole number in this range (100 = the normal size).</summary>
    public const int MinSizePercent = 40, MaxSizePercent = 300;

    /// <summary>One color per mood, as #RRGGBB.</summary>
    public Dictionary<string, string> MoodColors { get; set; } = new(DefaultColors);

    /// <summary>The character's size in percent of the normal size, 40 to 300.</summary>
    public int SizePercent { get; set; } = 100;

    /// <summary>
    /// Settings before 0.4.2 stored "small", "medium" or "large": read once and turned into a percentage (75, 100, 130),
    /// never written again.
    /// </summary>
    [JsonPropertyName("size")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OldSize
    {
        get => null;
        set
        {
            if (value is "small") SizePercent = 75;
            else if (value is "large") SizePercent = 130;
        }
    }

    /// <summary>The little labels under the character ("thinking…", "found it!").</summary>
    public bool ShowLabels { get; set; } = true;

    /// <summary>Smaller bounces and no wobble.</summary>
    public bool CalmAnimations { get; set; }

    /// <summary>Petting (moving the mouse back and forth over it) and poking (double-click).</summary>
    public bool Reactions { get; set; } = true;

    /// <summary>Small reactions to a finished download, a low battery or plugging in, and coming back to the PC.</summary>
    public bool EventReactions { get; set; } = true;

    /// <summary>Blinking, looking around, stretching and little hops while nothing is happening.</summary>
    public bool IdleTricks { get; set; } = true;

    /// <summary>Hats for Halloween, Christmas, New Year and the user's birthday (if they asked it to remember it).</summary>
    public bool SeasonalLooks { get; set; } = true;

    /// <summary>Walking along the screen now and then. Off by default: movement is distracting while working.</summary>
    public bool Wander { get; set; }

    /// <summary>light or dark.</summary>
    public string BubbleTheme { get; set; } = "light";

    /// <summary>Text size in the chat bubble, 11 to 18.</summary>
    public int BubbleTextSize { get; set; } = 13;

    [JsonIgnore]
    public double Scale => Math.Clamp(SizePercent, MinSizePercent, MaxSizePercent) / 100.0;

    /// <summary>Fills in anything missing or broken after loading, so a hand edit can't break the app.</summary>
    public void Repair()
    {
        MoodColors ??= new Dictionary<string, string>();
        foreach ((string mood, string color) in DefaultColors)
        {
            if (!MoodColors.TryGetValue(mood, out string? value) || !IsColor(value)) MoodColors[mood] = color;
        }
        // A custom name is checked when the character is shown (the folder may be gone): then it falls back to the blob.
        Character = (Character ?? "").Trim();
        if (!BuiltInCharacters.Contains(Character) && (CustomCharacter is not { Length: > 0 } name || !Assistant.Core.Characters.CharacterPacks.IsValidName(name)))
            Character = "blob";
        SizePercent = Math.Clamp(SizePercent, MinSizePercent, MaxSizePercent);
        if (BubbleTheme is not ("light" or "dark")) BubbleTheme = "light";
        BubbleTextSize = Math.Clamp(BubbleTextSize, 11, 18);
    }

    public Appearance Copy() => new()
    {
        Character = Character,
        MoodColors = new Dictionary<string, string>(MoodColors),
        SizePercent = SizePercent,
        ShowLabels = ShowLabels,
        CalmAnimations = CalmAnimations,
        Reactions = Reactions,
        EventReactions = EventReactions,
        IdleTricks = IdleTricks,
        SeasonalLooks = SeasonalLooks,
        Wander = Wander,
        BubbleTheme = BubbleTheme,
        BubbleTextSize = BubbleTextSize,
    };

    /// <summary>"#RRGGBB".</summary>
    public static bool IsColor(string? value) =>
        value is { Length: 7 } && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit);
}
