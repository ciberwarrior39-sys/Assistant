using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Assistant.Core.Actions;
using Assistant.Core.Diagnostics;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;

namespace Assistant.Core.Mail;

/// <summary>One message in the inbox, as much as a list needs: who, what about, when. Never the text of the message.</summary>
public sealed record MailLine(string From, string Subject, DateTime When);

/// <summary>What was found: the lines (newest first) and how many there are in all.</summary>
public sealed record MailFound(IReadOnlyList<MailLine> Lines, int Total);

/// <summary>What was asked: unread mail, or mail from someone (From) in the last few days. Explicit: the message said "mail".</summary>
public sealed record MailAsk(string From, bool Explicit);

/// <summary>Where the mail comes from: the app's read-only look into the inbox.</summary>
public interface IMailbox
{
    /// <summary>An address and a password are set.</summary>
    bool Connected { get; }

    Task<MailFound> UnreadAsync(CancellationToken ct);
    Task<MailFound> FromAsync(string name, int days, CancellationToken ct);
}

/// <summary>
/// "Any new emails?", "did Ivan reply?", "emails from the bank", and the Bulgarian "имам ли нова поща?", "писа ли ми
/// Иван?": answered by code from the inbox, on this computer. It only reads the list (sender, subject, date), never
/// opens a message, marks nothing as read, and can't send. Nothing about mail goes to the cloud.
/// </summary>
public static partial class MailAnswers
{
    const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    const string Mail = @"(?:e-?mails?|mails?|inbox|messages)";
    const string Name = @"(?<n>[\p{L}\p{N}][\p{L}\p{N} .'@_-]{0,40}?)";
    const int Days = 7;

    static readonly Regex[] Unread =
    [
        new($@"^(?:do\s+i\s+have\s+|have\s+i\s+got\s+|got\s+|is\s+there\s+|are\s+there\s+)?(?:any\s+)?(?:new|unread)\s+{Mail}$", Options),
        new($@"^(?:check|show|read|open\s+up)\s+(?:me\s+)?(?:my\s+|the\s+)(?:new\s+|unread\s+)?{Mail}$", Options),
        new($@"^(?:any|my)\s+{Mail}$|^what(?:'s|’s|\s+is)\s+(?:new\s+)?in\s+my\s+(?:inbox|e-?mail|mail)$", Options),
        new(@"^(?:имам\s+ли\s+)?(?:нов[аи]\s+|непрочетен[аи]\s+)(?:поща|писма|имейли|мейли|съобщения)$", Options),
        new(@"^(?:провери|покажи|виж)\s+(?:ми\s+)?(?:пощата|имейлите|мейлите|писмата)(?:\s+ми)?$", Options),
    ];

    // The message says "mail": enough by itself.
    static readonly Regex[] FromExplicit =
    [
        new($@"^(?:any\s+|new\s+|show\s+(?:me\s+)?|check\s+(?:for\s+)?)?(?:new\s+)?{Mail}\s+from\s+{Name}$", Options),
        new($@"^(?:did|has)\s+{Name}\s+(?:e-?mail(?:ed)?|mail(?:ed)?)\s+me(?:\s+back)?$", Options),
        new($@"^(?:do\s+i\s+have|have\s+i\s+got|is\s+there)\s+(?:an?\s+|any\s+)?(?:new\s+)?{Mail}\s+from\s+{Name}$", Options),
        new($@"^(?:имам\s+ли\s+)?(?:нов[аои]?\s+)?(?:поща|писма|писмо|имейли?|мейли?)\s+от\s+{Name}$", Options),
    ];

    // "Did Ivan reply?" could be about anything: taken as mail only when mail is connected.
    static readonly Regex[] FromImplied =
    [
        new($@"^(?:did|has)\s+{Name}\s+(?:repl(?:y|ied)|answer(?:ed)?|respond(?:ed)?|writ(?:e|ten)|wrote|g[eo]t(?:ten)?\s+back)(?:\s+(?:to\s+me|back|yet|to\s+my\s+e-?mail))*$", Options),
        new($@"^(?:any(?:thing)?|a\s+reply|an\s+answer|word)\s+from\s+{Name}(?:\s+yet)?$", Options),
        new($@"^(?:отговори|писа|пише)\s+ли\s+(?:ми\s+)?{Name}$|^{Name}\s+(?:отговори|писа)\s+ли(?:\s+ми)?$|^(?:нещо|отговор)\s+от\s+{Name}$", Options),
    ];

    public static MailAsk? Parse(string message)
    {
        string text = ActionParser.Courtesy(Spaces().Replace(message.Trim(), " "));
        if (text.Length == 0 || text.Length > 100) return null;
        if (Unread.Any(rule => rule.IsMatch(text))) return new MailAsk("", Explicit: true);
        foreach (Regex rule in FromExplicit)
        {
            if (rule.Match(text) is { Success: true } match) return new MailAsk(match.Groups["n"].Value.Trim(), Explicit: true);
        }
        foreach (Regex rule in FromImplied)
        {
            if (rule.Match(text) is { Success: true } match && !NotAPerson().IsMatch(match.Groups["n"].Value.Trim()))
                return new MailAsk(match.Groups["n"].Value.Trim(), Explicit: false);
        }
        return null;
    }

    /// <summary>The answer in words. Never logged; a count is.</summary>
    public static async Task<string> AnswerAsync(MailAsk ask, IMailbox mailbox, DateTime now, CancellationToken ct)
    {
        if (!mailbox.Connected)
            return "No mailbox is connected yet. In Settings > Calendar and mail, enter your address and an app password " +
                   "(for Gmail: Google Account > Security > 2-Step Verification > App passwords).";
        MailFound found;
        try
        {
            found = ask.From.Length == 0 ? await mailbox.UnreadAsync(ct) : await mailbox.FromAsync(ask.From, Days, ct);
        }
        catch (AuthenticationException)
        {
            DiagnosticLog.Info("Mail: the server refused the sign-in");
            return "The mail server refused the sign-in. It needs an app password, not your normal one (for Gmail: Google Account > " +
                   "Security > 2-Step Verification > App passwords). Check it in Settings > Calendar and mail.";
        }
        catch (Exception ex) when (ex is IOException or SocketException or ProtocolException or CommandException or TaskCanceledException
                                       or OperationCanceledException or ServiceNotConnectedException or ServiceNotAuthenticatedException or TimeoutException)
        {
            DiagnosticLog.Info($"Mail couldn't be checked ({ex.GetType().Name})");
            return "I couldn't reach your mailbox just now. Check the internet connection and the server name in Settings > Calendar and mail.";
        }
        DiagnosticLog.Info($"Mail: {found.Total} message(s) found"); // a count, never who or what

        if (ask.From.Length > 0)
        {
            if (found.Total == 0) return $"Nothing from {ask.From} in the last {Days} days.";
            return List($"From {ask.From} in the last {Days} days: {Count(found.Total, "email")}.", found, now, withSender: found.Lines.Select(line => line.From).Distinct().Count() > 1);
        }
        return found.Total == 0 ? "No unread emails." : List($"{Count(found.Total, "unread email")}.", found, now, withSender: true);
    }

    static string List(string head, MailFound found, DateTime now, bool withSender)
    {
        var text = new StringBuilder(head);
        if (found.Total > found.Lines.Count) text.Append($" The newest {found.Lines.Count}:");
        foreach (MailLine line in found.Lines)
        {
            string when = line.When.Date == now.Date ? $"today {line.When:HH:mm}"
                        : line.When.Date == now.Date.AddDays(-1) ? $"yesterday {line.When:HH:mm}"
                        : $"{line.When:d MMM}";
            text.Append($"\n- {(withSender ? line.From + ": " : "")}{(line.Subject.Length > 0 ? line.Subject : "(no subject)")} ({when})");
        }
        return text.ToString();
    }

    static string Count(int number, string what) => number == 1 ? $"1 {what}" : $"{number} {what}s";

    // "Did it reply", "did you answer": not somebody to look for.
    [GeneratedRegex(@"^(?:it|he|she|they|you|we|i|anyone|anybody|someone|somebody|that|this|the\s+\w+|той|тя|те|някой|ти)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NotAPerson();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}

/// <summary>
/// The inbox over IMAP (MailKit), opened read-only for each question and closed again. An app password is needed:
/// Gmail, Yahoo, iCloud, abv.bg and most others give one out; Outlook.com no longer does. Only the list of messages is
/// fetched (envelopes), never their text.
/// </summary>
public sealed class ImapMailbox(Func<(string Address, string Password, string Server)> settings) : IMailbox
{
    const int Shown = 6;
    static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    public bool Connected
    {
        get
        {
            (string address, string password, _) = settings();
            return address.Contains('@') && password.Length > 0;
        }
    }

    public Task<MailFound> UnreadAsync(CancellationToken ct) => FindAsync(SearchQuery.NotSeen, ct);

    public Task<MailFound> FromAsync(string name, int days, CancellationToken ct) =>
        FindAsync(SearchQuery.FromContains(name).And(SearchQuery.DeliveredAfter(DateTime.Today.AddDays(-days))), ct);

    async Task<MailFound> FindAsync(SearchQuery query, CancellationToken ct)
    {
        (string address, string password, string server) = settings();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Limit);
        using var client = new ImapClient { Timeout = (int)Limit.TotalMilliseconds };
        await client.ConnectAsync(Host(address, server), 993, SecureSocketOptions.SslOnConnect, limit.Token);
        await client.AuthenticateAsync(address.Trim(), password, limit.Token);
        IMailFolder inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly, limit.Token); // read-only: nothing is marked as read
        IList<UniqueId> ids = await inbox.SearchAsync(query, limit.Token);
        var newest = ids.OrderByDescending(id => id.Id).Take(Shown).ToList();
        var lines = new List<MailLine>();
        if (newest.Count > 0)
        {
            foreach (IMessageSummary item in await inbox.FetchAsync(newest, MessageSummaryItems.Envelope | MessageSummaryItems.InternalDate, limit.Token))
            {
                var sender = item.Envelope?.From?.Mailboxes.FirstOrDefault();
                string from = sender is null ? "(unknown)" : string.IsNullOrWhiteSpace(sender.Name) ? sender.Address : sender.Name;
                DateTime when = (item.InternalDate ?? item.Envelope?.Date ?? DateTimeOffset.Now).LocalDateTime;
                lines.Add(new MailLine(from, (item.Envelope?.Subject ?? "").Trim(), when));
            }
        }
        await client.DisconnectAsync(true, limit.Token);
        return new MailFound(lines.OrderByDescending(line => line.When).ToList(), ids.Count);
    }

    /// <summary>The server for an address: the one given, or the usual one for well-known providers, or "imap." + the domain.</summary>
    public static string Host(string address, string server)
    {
        if (server.Trim().Length > 0) return server.Trim();
        string domain = address.Contains('@') ? address[(address.LastIndexOf('@') + 1)..].Trim().ToLowerInvariant() : "";
        return domain switch
        {
            "gmail.com" or "googlemail.com" => "imap.gmail.com",
            "yahoo.com" or "ymail.com" => "imap.mail.yahoo.com",
            "icloud.com" or "me.com" or "mac.com" => "imap.mail.me.com",
            "abv.bg" => "imap.abv.bg",
            "mail.bg" => "imap.mail.bg",
            "outlook.com" or "hotmail.com" or "live.com" => "outlook.office365.com",
            _ => "imap." + domain,
        };
    }
}
