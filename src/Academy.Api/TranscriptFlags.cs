using System.Text.RegularExpressions;

namespace Academy.Api;

public static class TranscriptFlags
{
    private static Regex Pattern(string pattern) => new(pattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromSeconds(1));

    private static readonly (string Code, Regex Pattern)[] Rules =
    [
        ("phone", Pattern(@"\b(mobile|cell|cellphone|phone|telephone|personal|private|direct|home|work|landline|contact|whatsapp|viber) (number|no|num)\b")),
        ("phone", Pattern(@"\b(my|your|ur) number\b")),
        ("phone", Pattern(@"\b(what is|whats|what's|can i (have|get)|give me|send me|share|tell me) (your|my|ur) ((phone|mobile|cell|whatsapp|personal) )?(number|digits)\b")),
        ("phone", Pattern(@"\b(ring|phone|text|sms|msg|buzz|message) (me|you)\b")),
        ("phone", Pattern(@"\bgive (me|you) a (call|ring|missed call)\b")),
        ("phone", Pattern(@"\bcall (me|you) (on|at|later|tonight|tomorrow|after|outside|directly|back|privately|\+?\d+)\b")),
        ("phone", Pattern(@"\b((on|by|over) the phone|talk on the phone|voice note|voice message)\b")),

        ("email", Pattern(@"\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b")),
        ("email", Pattern(@"\b(my|your|share|send|give|what is|whats|what's|write|have|drop|type|spell) (\w+ ){0,3}(e[- ]?mail|mail|gmail|hotmail|yahoo|outlook|icloud|proton ?mail)\b")),
        ("email", Pattern(@"\b(gmail|hotmail|yahoo mail|outlook\.com|icloud|proton ?mail)\b")),
        ("email", Pattern(@"\b(send|write|drop|shoot) (me|you) an? (e[- ]?mail|mail|message)\b")),
        ("email", Pattern(@"\be[- ]?mail (me|you)\b")),

        ("platform", Pattern(@"\b(whatsapp|telegram|signal app|on signal|viber|imo|botim|wechat|line app|kik|facebook messenger|on messenger|messenger app|discord|skype|snapchat|threema)\b")),
        ("platform", Pattern(@"\b(instagram|insta|facebook|fb|tiktok|twitter|x dot com|youtube channel|linkedin|pinterest|reddit|tumblr|on threads)\b")),
        ("platform", Pattern(@"\b(on|via|over|through|a) (zoom|google meet|meet link|teams|microsoft teams|facetime|duo|skype|jitsi|webex)\b")),
        ("platform", Pattern(@"\b(zoom (call|meeting|link|id)|meet link)\b")),
        ("platform", Pattern(@"\b(preply|italki|cambly|superprof|fiverr|upwork|outschool|wyzant|udemy)\b")),

        ("offplatform", Pattern(@"\b(reach|reach out to|get in touch with|contact|add|dm|pm|inbox|ping|email|whatsapp) (me|you)\b")),
        ("offplatform", Pattern(@"\b(follow|find|message) (me|you) (on|at|via|through|outside|directly)\b")),
        ("offplatform", Pattern(@"\b(how|where) (can|do|could|should) (i|we) (reach|contact|find|talk to|message|call|get hold of) (you|me)\b")),
        ("offplatform", Pattern(@"\b(do|did) you have (an? )?(whatsapp|instagram|insta|telegram|facebook|snapchat|tiktok|phone|mobile|number|email|account)\b")),
        ("offplatform", Pattern(@"\b(what is|whats|what's|tell me|give me|send me|share) your (whatsapp|insta|instagram|telegram|snap|snapchat|tiktok|facebook|account|handle|username|user name|profile|channel|contacts?|contact details|contact info)\b")),
        ("offplatform", Pattern(@"\b(search|look|look up|google) (for )?(my|me|your) (name|account|channel|profile|username)\b")),
        ("offplatform", Pattern(@"\b(look me up|find me on|follow me on|subscribe to my|my (channel|profile|account|handle|username))\b")),
        ("offplatform", Pattern(@"\b(add|send|accept) (me|my) (as a )?(friend|request|follow)\b|\bfriend request\b")),
        ("offplatform", Pattern(@"\b(let's|lets|we can|we could|shall we|why don't we|i can|i could) (\w+ ){0,3}(talk|chat|meet|continue|study|learn|have (lessons|classes|sessions)) (\w+ ){0,3}(outside|privately|directly|on my own|separately|by ourselves)\b")),
        ("offplatform", Pattern(@"\b(outside|beyond|instead of|away from) ((the|this|our) )?(lesson|class|session|academy|platform|app|application|website|site|system)\b")),
        ("offplatform", Pattern(@"\b(without|off) ((the|this|our) )?(academy|platform|app|application|website|site|system)\b")),
        ("offplatform", Pattern(@"\b(off[- ]?platform|off the record)\b")),
        ("offplatform", Pattern(@"\b(leave|quit|cancel|stop) ((the|this) )?(academy|platform|app|website|subscription)\b")),
        ("offplatform", Pattern(@"\b(private|personal|separate|own|outside) (lessons?|classes|class|sessions?|tutoring|teaching)\b")),
        ("offplatform", Pattern(@"\b(teach|tutor|study with) you (myself|privately|directly|personally|outside|on my own)\b")),
        ("offplatform", Pattern(@"\bmy (own )?(academy|school|website|site|classes|students|centre|center|institute)\b")),
        ("offplatform", Pattern(@"\b(tell|ask|have|let) your (mom|mum|mother|dad|father|parents?|family|brother|sister|uncle|guardian) (to )?(call|contact|text|message|email|reach|add|talk to) (me|my)\b")),
        ("offplatform", Pattern(@"\bgive your (mom|mum|mother|dad|father|parents) my\b")),
        ("offplatform", Pattern(@"\byour (mom|mum|dad|father|mother|parents?)('s)? (number|phone|whatsapp|email)\b")),

        ("payment", Pattern(@"\b(pay|send|transfer) (me|it to me|the money to me) (directly|instead|yourself|personally|in cash|cash)\b|\bpay me\b")),
        ("payment", Pattern(@"\b((bank|wire) transfer|vodafone cash|instapay|fawry|paypal|western union|moneygram|venmo|zelle|cash ?app|wise transfer|revolut|stc pay|payoneer|crypto|bitcoin|usdt)\b")),
        ("payment", Pattern(@"\b(cheaper|lower|better|half|discount|special|reduced) (prices?|rates?|fees?|deal|cost)\b")),
        ("payment", Pattern(@"\bcheaper (lessons?|classes|class|sessions?|tutoring)\b")),
        ("payment", Pattern(@"\b(half (the )?price|for less|no (fees|commission)|without (the )?(fees|commission|middleman))\b")),
        ("payment", Pattern(@"\b(academy|platform|website|they) (takes|charge|charges|is expensive|is too expensive|keeps|cuts|commission|fees?)\b")),

        ("secrecy", Pattern(@"\b(don't|do not|dont|never) (tell|say|mention|inform) (\w+ ){0,3}(anyone|anybody|your (parents|mom|mum|dad|father|mother|family)|the (academy|admins?|support|supervisor|office|company))\b")),
        ("secrecy", Pattern(@"\b(don't|do not|dont|never) let (\w+ ){0,3}(anyone|anybody|your (parents|mom|mum|dad|father|mother|family)|the (academy|admins?|support|supervisor|office|company)) know\b")),
        ("secrecy", Pattern(@"\bkeep (this|it|that) (between us|a secret|secret|private|to yourself|quiet)\b")),
        ("secrecy", Pattern(@"\b(our (little )?secret|between (you and me|us))\b")),
        ("secrecy", Pattern(@"\b(they|the academy|someone|support|admins?)( are| is|'re|'s)? (listening|recording|watching|monitoring)\b")),
        ("secrecy", Pattern(@"\b(delete|remove|clear) (this|the|my|our) (messages?|chat|number|conversation)\b")),
        ("secrecy", Pattern(@"\b(turn off|mute|switch off) (the )?(mic|microphone|recording|camera) (so|before|and) (i|we) (tell|say|give)\b")),
    ];

    private static readonly (Regex Pattern, string Replacement)[] BrandSpellings =
    [
        (Pattern(@"\b(what'?s? app|what sapp|watsapp|wats app)\b"), "whatsapp"),
        (Pattern(@"\binsta gram\b"), "instagram"),
        (Pattern(@"\btik tok\b"), "tiktok"),
        (Pattern(@"\bsnap chat\b"), "snapchat"),
        (Pattern(@"\bface book\b"), "facebook"),
        (Pattern(@"\bg mail\b"), "gmail"),
    ];

    private static readonly Regex Whitespace = Pattern(@"\s+");
    private static readonly Regex Punctuation = Pattern(@"[,!?;]|\.(\s|$)");
    private static readonly Regex PhoneLike = Pattern(@"\+?\d[\d\s().\-]*\d");
    private static readonly Regex DatesAndTimes = Pattern(@"\b(\d{4}[-/.]\d{1,2}[-/.]\d{1,2}|\d{1,2}[-/.]\d{1,2}[-/.]\d{2,4}|\d{1,2}:\d{2}(:\d{2})?)\b");
    private static readonly Regex SpokenDigit = Pattern(@"\b((double|triple) )?(zero|oh|o|one|two|three|four|five|six|seven|eight|nine|[0-9])\b");
    private static readonly Regex SpokenPlus = Pattern(@"\bplus\s+(\d)");
    private static readonly Regex SpokenEmail = Pattern(@"\b[a-z0-9]+( (dot|underscore|dash|hyphen) [a-z0-9]+)* at( sign)? [a-z0-9]+( (dot|dash|hyphen) [a-z0-9]+)* dot (com|net|org|co|uk|eg|sa|ae|us|ca|au|io|me|info|edu)\b");
    private static readonly Regex EmailSymbol = Pattern(@" (at sign|at|dot|underscore|dash|hyphen) ");

    public static IReadOnlyList<string> Scan(string text)
    {
        var normalized = Normalize(text);
        var found = new List<string>();
        if (HasPhoneNumber(text) || HasPhoneNumber(normalized)) found.Add("phone");
        foreach (var rule in Rules)
        {
            if (!found.Contains(rule.Code) && (rule.Pattern.IsMatch(text) || rule.Pattern.IsMatch(normalized)))
                found.Add(rule.Code);
        }
        return found;
    }

    private static string Normalize(string text)
    {
        var normalized = Whitespace.Replace(text.ToLowerInvariant().Replace('’', '\'').Replace('‘', '\''), " ");
        foreach (var spelling in BrandSpellings)
            normalized = spelling.Pattern.Replace(normalized, spelling.Replacement);
        normalized = SpokenEmail.Replace(normalized, match => EmailSymbol.Replace(match.Value, symbol => symbol.Value.Trim() switch
        {
            "at" or "at sign" => "@",
            "dot" => ".",
            "underscore" => "_",
            _ => "-"
        }));
        normalized = Punctuation.Replace(normalized, " ");
        normalized = Whitespace.Replace(normalized, " ");
        normalized = SpokenDigit.Replace(normalized, match =>
        {
            var parts = match.Value.Split(' ');
            var digit = parts[^1] switch
            {
                "zero" or "oh" or "o" => '0',
                "one" => '1',
                "two" => '2',
                "three" => '3',
                "four" => '4',
                "five" => '5',
                "six" => '6',
                "seven" => '7',
                "eight" => '8',
                "nine" => '9',
                _ => parts[^1][0]
            };
            return new string(digit, parts[0] == "double" ? 2 : parts[0] == "triple" ? 3 : 1);
        });
        return SpokenPlus.Replace(normalized, "+$1");
    }

    private static bool HasPhoneNumber(string text)
    {
        // Date/time fields must not merge into a phone number across their separator.
        var withoutDates = DatesAndTimes.Replace(text, " date ");
        foreach (Match match in PhoneLike.Matches(withoutDates))
        {
            var digits = match.Value.Count(char.IsDigit);
            if (digits >= 9 || (match.Value.StartsWith('+') && digits >= 8)) return true;
        }
        return false;
    }
}
