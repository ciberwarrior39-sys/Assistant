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

/// <summary>One message in an inbox, as much as a list needs: which mailbox, who, what about, when. Never the text.</summary>
public sealed record MailLine(string From, string Subject, DateTime When, string Account = "");

/// <summary>
/// What was found: the lines (newest first), how many there are in all, and the mailboxes that couldn't be read, with
/// why ("sign-in": the password was refused; "unreachable": no answer).
/// </summary>
public sealed record MailFound(IReadOnlyList<MailLine> Lines, int Total, IReadOnlyList<(string Account, string Why)>? Failed = null)
{
    public IReadOnlyList<(string Account, string Why)> Problems => Failed ?? [];
}

/// <summary>A button for "check my email", offered by cloud chat: answered like "any new emails?" or "emails from …".</summary>
public sealed record CheckMail(string From = "", string Account = "", bool Latest = false) : Assistant.Core.Actions.ActionIntent;

/// <summary>
/// What was asked: unread mail, or mail from someone (From) in the last few days, in one mailbox (Account, by its
/// label) or in all of them (""). Explicit: the message said "mail".
/// </summary>
/// Latest: the newest message, read or not ("when did I last get an email?", "last email from Ivan"), any date.
public sealed record MailAsk(string From, bool Explicit, string Account = "", bool Latest = false);

/// <summary>Where the mail comes from: the app's read-only look into the user's mailboxes.</summary>
public interface IMailbox
{
    /// <summary>The labels of the mailboxes that are set up ("work", "personal"). Empty: none.</summary>
    IReadOnlyList<string> Accounts { get; }

    bool Connected => Accounts.Count > 0;

    /// <summary>Unread mail in one mailbox, or in all of them when account is "".</summary>
    Task<MailFound> UnreadAsync(string account, CancellationToken ct);

    Task<MailFound> FromAsync(string name, int days, string account, CancellationToken ct);

    /// <summary>The newest messages, read or not, from anyone (name "") or from someone, whenever they came.</summary>
    Task<MailFound> LatestAsync(string name, string account, CancellationToken ct);
}

/// <summary>
/// "Any new emails?", "did Ivan reply?", "emails from the bank", "any new work emails?", and the Bulgarian "имам ли
/// нова поща?", "писа ли ми Иван?": answered by code from the inboxes, on this computer. It only reads the list (sender,
/// subject, date), never opens a message, marks nothing as read, and can't send. Nothing about mail goes to the cloud.
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
        // Short and ending in "my email": what's in it, even when speech recognition misheard the start
        // ("watching my email" for "what's in my email"). Nothing here can send or delete, so a wrong guess costs nothing.
        new($@"^(?:[\p{{L}}']+\s+){{0,2}}(?:in\s+|on\s+|at\s+)?my\s+(?:e-?mails?|mails?|inbox)$", Options),
        new(@"^(?:имам\s+ли\s+)?(?:нов[аи]\s+|непрочетен[аи]\s+)(?:поща|писма|имейли|мейли|съобщения)$", Options),
        new(@"^(?:провери|покажи|виж)\s+(?:ми\s+)?(?:пощата|имейлите|мейлите|писмата)(?:\s+ми)?$", Options),
    ];

    // The newest message, read or not: "when did I last get an email?", "my latest email", "last mail from Ivan".
    static readonly Regex[] Newest =
    [
        new($@"^when\s+(?:was|is)\s+(?:the\s+)?last\s+time\s+i\s+(?:got|received|had|have\s+got)\s+(?:an?\s+|any\s+)?{Mail}(?:\s+from\s+{Name})?$", Options),
        new($@"^when\s+did\s+i\s+(?:last\s+)?(?:get|receive)\s+(?:an?\s+|any\s+|my\s+last\s+)?{Mail}(?:\s+from\s+{Name})?(?:\s+last)?$", Options),
        new($@"^(?:what(?:'s|’s|\s+is|\s+was)\s+|show\s+(?:me\s+)?|read\s+(?:me\s+)?|who\s+sent\s+)?(?:my\s+|the\s+)?(?:latest|last|newest|most\s+recent)\s+{Mail}(?:\s+from\s+{Name})?$", Options),
        new(@"^who\s+(?:e-?mailed|mailed|wrote\s+to|messaged)\s+me\s+last$|^who\s+(?:e-?mailed|mailed|wrote\s+to)\s+me\s+most\s+recently$", Options),
        new($@"^кога\s+(?:ми\s+)?(?:е\s+)?(?:дойде|дошл[оа]|пристигна|пристигнал[оа]|получих|е\s+дошл[оа])\s+(?:последн\w+\s+)?(?:писмо|имейл|поща|мейл)(?:\s+от\s+{Name})?$", Options),
        new($@"^(?:кое\s+е\s+|кой\s+е\s+|покажи\s+(?:ми\s+)?)?(?:последн\w+|най-нов\w+)\s+(?:ми\s+)?(?:писмо|имейл|мейл)(?:\s+от\s+{Name})?$", Options),
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

    /// <param name="accounts">The mailboxes' labels: "any new work emails?" is about the one called "work".</param>
    public static MailAsk? Parse(string message, IReadOnlyList<string>? accounts = null)
    {
        string text = ActionParser.Courtesy(Spaces().Replace(message.Trim(), " "));
        if (text.Length == 0 || text.Length > 100) return null;

        // A mailbox named in the message is noted and taken out, and the rest is read as usual:
        // "any new work emails" -> "any new emails", "did Ivan reply on my work email" -> "did Ivan reply".
        string account = "";
        foreach (string label in (accounts ?? []).Where(label => label.Trim().Length > 0).OrderByDescending(label => label.Length))
        {
            string word = Regex.Escape(label.Trim());
            string[] shapes =
            [
                $@"\s+(?:in|on|at|from|for)\s+(?:my\s+|the\s+)?{word}(?:\s+(?:e-?mail|mail|inbox|account|mailbox))?$",
                $@"\s+(?:в|във|на|от)\s+(?:пощата\s+)?(?:за\s+)?{word}(?:\s+(?:поща|пощата|имейл|акаунт))?$",
                $@"(?<=^|\s){word}\s+(?={Mail}\b|поща|имейл)",
            ];
            foreach (string shape in shapes)
            {
                string without = Regex.Replace(text, shape, " ", Options).Trim();
                if (without == text) continue;
                account = label.Trim();
                text = Spaces().Replace(without, " ");
                break;
            }
            if (account.Length > 0) break;
        }

        // "the last mail I've gotten", "the latest email I received": the end says nothing more.
        string newestText = Received().Replace(text, "").Trim();
        foreach (Regex rule in Newest)
        {
            if (rule.Match(newestText) is { Success: true } match)
                return new MailAsk(match.Groups["n"].Success ? Sender(match) : "", Explicit: true, account, Latest: true);
        }
        if (Unread.Any(rule => rule.IsMatch(text))) return new MailAsk("", Explicit: true, account);
        foreach (Regex rule in FromExplicit)
        {
            if (rule.Match(text) is { Success: true } match && Sender(match) is { Length: > 0 } sender) return new MailAsk(sender, Explicit: true, account);
        }
        foreach (Regex rule in FromImplied)
        {
            if (rule.Match(text) is { Success: true } match && Sender(match) is { Length: > 0 } sender && !NotAPerson().IsMatch(sender))
                return new MailAsk(sender, Explicit: account.Length > 0, account);
        }
        return null;
    }

    [GeneratedRegex(@"\s+(?:that\s+|which\s+)?(?:i|i've|i’ve|i\s+have|i\s+had)\s+(?:got|gotten|received|had|get)(?:\s+(?:in\s+my\s+inbox|today|so\s+far))?$|\s+(?:in|to)\s+my\s+inbox$|\s+(?:that\s+)?(?:came|arrived)(?:\s+in)?$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Received();

    // Who to look for, without words about where ("GitHub in my inbox" -> "GitHub").
    static string Sender(Match match) => Where().Replace(match.Groups["n"].Value.Trim(), "").Trim();

    [GeneratedRegex(@"\s+(?:in|on|at|into)\s+(?:my\s+|the\s+)?(?:inbox|e-?mails?|mail(?:box)?|account)$|\s+(?:в|във)\s+(?:пощата|имейла)(?:\s+ми)?$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Where();

    /// <summary>The label of the mailbox a word means ("work" for "Work", "the work one"), or "" for all of them.</summary>
    public static string AccountFor(string said, IReadOnlyList<string> accounts)
    {
        string word = said.Trim();
        if (word.Length == 0) return "";
        return accounts.FirstOrDefault(label => label.Equals(word, StringComparison.OrdinalIgnoreCase))
               ?? accounts.FirstOrDefault(label => word.Contains(label, StringComparison.OrdinalIgnoreCase) || label.Contains(word, StringComparison.OrdinalIgnoreCase))
               ?? "";
    }

    /// <summary>The answer in words. Never logged; a count is.</summary>
    public static async Task<string> AnswerAsync(MailAsk ask, IMailbox mailbox, DateTime now, CancellationToken ct)
    {
        if (!mailbox.Connected)
            return "No mailbox is connected yet. In Settings > Calendar and mail, add one with your address and an app password " +
                   "(for Gmail: Google Account > Security > 2-Step Verification > App passwords).";
        string account = AccountFor(ask.Account, mailbox.Accounts);
        bool several = account.Length == 0 && mailbox.Accounts.Count > 1;

        MailFound found = ask.Latest ? await mailbox.LatestAsync(ask.From, account, ct)
                        : ask.From.Length == 0 ? await mailbox.UnreadAsync(account, ct)
                        : await mailbox.FromAsync(ask.From, Days, account, ct);
        DiagnosticLog.Info($"Mail: {found.Total} message(s) found in {(account.Length > 0 ? 1 : mailbox.Accounts.Count)} mailbox(es), {found.Problems.Count} couldn't be read");

        int asked = account.Length > 0 ? 1 : mailbox.Accounts.Count;
        string trouble = Trouble(found.Problems);
        if (found.Problems.Count >= asked) return trouble; // none could be read

        string where = account.Length > 0 ? $" in {account}" : "";
        if (ask.Latest)
        {
            string newestAnswer;
            if (found.Lines.Count == 0)
            {
                newestAnswer = ask.From.Length > 0 ? $"I couldn't find any email from {ask.From}{where}." : $"Your inbox{where} is empty.";
            }
            else
            {
                MailLine newest = found.Lines[0];
                string box = several && newest.Account.Length > 0 ? $" (in {newest.Account})" : "";
                string subject = newest.Subject.Length > 0 ? newest.Subject : "(no subject)";
                newestAnswer = ask.From.Length > 0
                    ? $"The last email from {ask.From}{where} came {When(newest.When, now)}{box}: {subject}"
                    : $"Your newest email{where} came {When(newest.When, now)}{box}, from {newest.From}: {subject}";
                var earlier = found.Lines.Skip(1).Take(3).ToList();
                if (earlier.Count > 0) newestAnswer += List("\n\nBefore that:", new MailFound(earlier, earlier.Count), now,
                                                             withSender: ask.From.Length == 0, withAccount: several);
            }
            return trouble.Length > 0 ? newestAnswer + "\n\n" + trouble : newestAnswer;
        }
        string answer;
        if (ask.From.Length > 0)
        {
            answer = found.Total == 0
                ? $"Nothing from {ask.From}{where} in the last {Days} days."
                : List($"From {ask.From}{where} in the last {Days} days: {Count(found.Total, "email")}.", found, now,
                       withSender: found.Lines.Select(line => line.From).Distinct().Count() > 1, withAccount: several);
        }
        else
        {
            string split = several && found.Total > 0
                ? " (" + string.Join(", ", found.Lines.GroupBy(line => line.Account).Select(group => $"{group.Key} {group.Count()}")) + ")"
                : "";
            answer = found.Total == 0
                ? $"No unread emails{where}."
                : List($"{Count(found.Total, "unread email")}{where}.", found, now, withSender: true, withAccount: several);
            if (found.Total > 0 && several && found.Total == found.Lines.Count) answer = answer.Replace($"{Count(found.Total, "unread email")}.", $"{Count(found.Total, "unread email")}{split}.");
        }
        return trouble.Length > 0 ? answer + "\n\n" + trouble : answer;
    }

    // "today at 14:32", "yesterday at 09:10", "on Monday, 23 November at 18:05 (3 days ago)".
    static string When(DateTime at, DateTime now)
    {
        if (at.Date == now.Date) return $"today at {at:HH:mm}";
        if (at.Date == now.Date.AddDays(-1)) return $"yesterday at {at:HH:mm}";
        int days = (now.Date - at.Date).Days;
        return $"on {at:dddd, d MMMM}{(at.Year != now.Year ? $" {at.Year}" : "")} at {at:HH:mm} ({days} days ago)";
    }

    // What to say about the mailboxes that couldn't be read.
    static string Trouble(IReadOnlyList<(string Account, string Why)> problems)
    {
        if (problems.Count == 0) return "";
        var refused = problems.Where(problem => problem.Why == "sign-in").Select(problem => problem.Account).ToList();
        var silent = problems.Where(problem => problem.Why != "sign-in").Select(problem => problem.Account).ToList();
        var text = new StringBuilder();
        if (refused.Count > 0)
            text.Append($"The mail server refused the sign-in for {string.Join(" and ", refused)}. It needs an app password, not your normal one " +
                        "(for Gmail: Google Account > Security > 2-Step Verification > App passwords). Check it in Settings > Calendar and mail.");
        if (silent.Count > 0)
            text.Append($"{(text.Length > 0 ? " " : "")}I couldn't reach {string.Join(" and ", silent)} just now. Check the internet connection " +
                        "and the server name in Settings > Calendar and mail.");
        return text.ToString();
    }

    static string List(string head, MailFound found, DateTime now, bool withSender, bool withAccount = false)
    {
        var text = new StringBuilder(head);
        if (found.Total > found.Lines.Count) text.Append($" The newest {found.Lines.Count}:");
        foreach (MailLine line in found.Lines)
        {
            string when = line.When.Date == now.Date ? $"today {line.When:HH:mm}"
                        : line.When.Date == now.Date.AddDays(-1) ? $"yesterday {line.When:HH:mm}"
                        : $"{line.When:d MMM}";
            text.Append($"\n- {(withAccount && line.Account.Length > 0 ? $"[{line.Account}] " : "")}{(withSender ? line.From + ": " : "")}{(line.Subject.Length > 0 ? line.Subject : "(no subject)")} ({when})");
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
/// The user's mailboxes (Settings > Calendar and mail), each looked into over IMAP (MailKit), opened read-only for
/// each question and closed again, several at once. An app password is needed: Gmail, Yahoo, iCloud, abv.bg and most
/// others give one out; Outlook.com no longer does. Only the list of messages is fetched (envelopes), never their text.
/// A mailbox that can't be read is reported by its label; the others still answer.
/// </summary>
public sealed class ImapMailbox(Func<IReadOnlyList<MailAccountSettings>> settings) : IMailbox
{
    const int Shown = 6;
    static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    public IReadOnlyList<string> Accounts => Usable().Select(account => account.Label).ToList();

    List<MailAccountSettings> Usable() => settings().Where(account => account.Address.Contains('@') && account.Password.Length > 0).ToList();

    public Task<MailFound> UnreadAsync(string account, CancellationToken ct) => FindAllAsync(SearchQuery.NotSeen, account, ct);

    // Whenever it came: no date limit. Only the newest few are fetched.
    public Task<MailFound> LatestAsync(string name, string account, CancellationToken ct)
    {
        if (name.Trim().Length == 0) return FindAllAsync(SearchQuery.All, account, ct);
        string joined = name.Replace(" ", "");
        return FindAllAsync(joined == name ? SearchQuery.FromContains(name) : SearchQuery.Or(SearchQuery.FromContains(name), SearchQuery.FromContains(joined)), account, ct);
    }

    // "git hub" and "soft uni" are usually written "GitHub" and "SoftUni" by the sender, so a name in several words is
    // also looked for with the spaces taken out.
    public Task<MailFound> FromAsync(string name, int days, string account, CancellationToken ct)
    {
        string joined = name.Replace(" ", "");
        SearchQuery who = joined == name ? SearchQuery.FromContains(name) : SearchQuery.Or(SearchQuery.FromContains(name), SearchQuery.FromContains(joined));
        return FindAllAsync(who.And(SearchQuery.DeliveredAfter(DateTime.Today.AddDays(-days))), account, ct);
    }

    async Task<MailFound> FindAllAsync(SearchQuery query, string account, CancellationToken ct)
    {
        var chosen = Usable().Where(item => account.Length == 0 || item.Label.Equals(account, StringComparison.OrdinalIgnoreCase)).ToList();
        var results = await Task.WhenAll(chosen.Select(async item =>
        {
            try
            {
                return (Found: await FindAsync(query, item, ct), Why: "");
            }
            catch (AuthenticationException ex)
            {
                // The server's own reason ("Invalid credentials", "Application-specific password required") tells a
                // normal password from a mistyped app password. It names neither the address nor the password.
                DiagnosticLog.Info($"Mail: the server refused the sign-in ({DiagnosticLog.WithoutPaths(ex.Message)})");
                return (Found: (MailFound?)null, Why: "sign-in");
            }
            catch (Exception ex) when (ex is IOException or SocketException or ProtocolException or CommandException or TaskCanceledException
                                           or OperationCanceledException or ServiceNotConnectedException or ServiceNotAuthenticatedException
                                           or TimeoutException)
            {
                DiagnosticLog.Info($"Mail couldn't be checked ({ex.GetType().Name})");
                return (Found: (MailFound?)null, Why: "unreachable");
            }
        }));

        var lines = new List<MailLine>();
        var failed = new List<(string, string)>();
        int total = 0;
        for (int i = 0; i < chosen.Count; i++)
        {
            if (results[i].Found is { } found)
            {
                lines.AddRange(found.Lines.Select(line => line with { Account = chosen[i].Label }));
                total += found.Total;
            }
            else
            {
                failed.Add((chosen[i].Label, results[i].Why));
            }
        }
        return new MailFound(lines.OrderByDescending(line => line.When).Take(Shown * Math.Max(1, Math.Min(chosen.Count, 2))).ToList(), total, failed);
    }

    static async Task<MailFound> FindAsync(SearchQuery query, MailAccountSettings account, CancellationToken ct)
    {
        (string address, string password, string server) = (account.Address, account.Password, account.Server);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Limit);
        using var client = new ImapClient { Timeout = (int)Limit.TotalMilliseconds };
        await client.ConnectAsync(Host(address, server), 993, SecureSocketOptions.SslOnConnect, limit.Token);
        // Google shows app passwords in groups of four, and copying them can bring along spaces of any kind
        // (including non-breaking ones). None of them are part of the password.
        string clean = new(password.Where(letter => !char.IsWhiteSpace(letter)).ToArray());
        await client.AuthenticateAsync(address.Trim(), clean, limit.Token);
        IMailFolder inbox = client.Inbox ?? throw new ImapProtocolException("The server didn't offer an inbox.");
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

/// <summary>One mailbox as the mail code needs it: its label, address, app password and (optional) server.</summary>
public sealed record MailAccountSettings(string Label, string Address, string Password, string Server);
