namespace Assistant.Core.Understanding;

/// <summary>
/// The safe middle level for cloud chat: after answers the app kept private (the calendar, mail, files), the cloud
/// model gets only what they were about ("their calendar"), never what was asked or answered, so a message that
/// follows on ("should I go?") can be answered sensibly: by asking for the details or pointing back to the PC.
/// </summary>
public static class CloudTopics
{
    // What each private part of the app is about, by the last part of its route ("followup>calendar", "tool:search_files").
    static readonly Dictionary<string, string> Topics = new(StringComparer.Ordinal)
    {
        ["calendar"] = "their calendar",
        ["mail"] = "their email",
        ["search_files"] = "files and folders on their PC",
        ["open_file"] = "files and folders on their PC",
        ["describe_files"] = "files and folders on their PC",
        ["recent_activity"] = "what they did on their PC recently",
        ["conversation_log"] = "their earlier conversations with the assistant",
        ["remember"] = "things they asked the assistant to remember",
        ["recall"] = "things they asked the assistant to remember",
        ["forget"] = "things they asked the assistant to remember",
        ["memory"] = "things they asked the assistant to remember",
        ["organizer"] = "their notes, to-do list or reminders",
        ["routine"] = "their routines",
        ["guide"] = "where to click in an app on their screen",
        ["look_at_screen"] = "what's on their screen",
        ["action"] = "doing something on their PC (opening an app, a setting)",
    };

    const int MaxTopics = 2;

    /// <summary>
    /// The note for the cloud model, or "" when the latest answers weren't private ones. Walks back from the latest
    /// answer while answers were private, so a topic from before a cloud answer doesn't come back later.
    /// </summary>
    public static string Hint(IReadOnlyList<Turn> recent)
    {
        var topics = new List<string>();
        for (int i = recent.Count - 1; i >= 0 && topics.Count < MaxTopics; i--)
        {
            string route = recent[i].Route;
            if (route.StartsWith("cloud", StringComparison.Ordinal) || route == "local") break; // the cloud saw that one
            if (Topics.TryGetValue(Last(route), out string? topic) && !topics.Contains(topic)) topics.Add(topic);
        }
        if (topics.Count == 0) return "";

        return "\n\nTopic note: just before this message, the assistant on the user's PC answered them about "
               + string.Join(", and before that about ", topics)
               + ". That part stays on their PC: you weren't told what was asked or answered. If the new message follows on "
               + "from it, don't guess the details: say you can't see them, and ask the user to tell you what matters, or "
               + "suggest asking the assistant again on the PC (for example \"and tomorrow?\" after a calendar answer).";
    }

    // "followup>calendar" → "calendar", "local>tool:search_files" → "search_files".
    static string Last(string route)
    {
        int cut = Math.Max(route.LastIndexOf('>'), route.LastIndexOf(':'));
        return cut >= 0 ? route[(cut + 1)..] : route;
    }
}
