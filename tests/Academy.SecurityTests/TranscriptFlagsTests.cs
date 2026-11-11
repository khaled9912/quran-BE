using System.Globalization;
using Academy.Api;
using Xunit;

namespace Academy.SecurityTests;

public sealed class TranscriptFlagsTests
{
    [Theory]
    [InlineData("can you share your mobile number", "phone")]
    [InlineData("share your email", "email")]
    [InlineData("share your e mail address", "email")]
    [InlineData("can you share your mobile number and share your email", "phone,email")]
    [InlineData("SHARE YOUR EMAIL", "email")]
    [InlineData("Share, your\n\temail!", "email")]
    [InlineData("what’s your gmail", "email")]
    [InlineData("my number is zero one five zero one two two three one one five", "phone")]
    [InlineData("zero one five zero one two two three one one five", "phone")]
    [InlineData("it's double five, triple seven, one two three four", "phone")]
    [InlineData("plus two zero one zero five five six seven", "phone")]
    [InlineData("oh one o two three four five six seven", "phone")]
    [InlineData("zero 1 five 0 one two 2 three 1 one 5", "phone")]
    [InlineData("double 5 triple 7 1234", "phone")]
    [InlineData("+20 (100) 123-4567", "phone")]
    [InlineData("555.123.4567", "phone")]
    [InlineData("123456789", "phone")]
    [InlineData("+12345678", "phone")]
    [InlineData("2026-11-01 10:30. Then dial +20 100 123 4567", "phone")]
    [InlineData("what is your cell number", "phone")]
    [InlineData("can I have your digits", "phone")]
    [InlineData("give me a missed call", "phone")]
    [InlineData("call me after class", "phone")]
    [InlineData("call me +12345678", "phone")]
    [InlineData("call me tonight", "phone")]
    [InlineData("text me", "phone")]
    [InlineData("send a voice note", "phone")]
    [InlineData("we can talk on the phone", "phone")]
    [InlineData("write your personal number here", "phone")]
    [InlineData("my address is john.smith+lessons@example.org", "email")]
    [InlineData("john dot smith at gmail dot com", "email")]
    [InlineData("john underscore smith at sign example dot org", "email")]
    [InlineData("john dash smith at example dot co dot uk", "email")]
    [InlineData("john at example dot org", "email")]
    [InlineData("john at g mail dot com", "email")]
    [InlineData("please share your private e-mail address", "email")]
    [InlineData("send me an email", "email")]
    [InlineData("send me a mail", "email")]
    [InlineData("drop me a message", "email")]
    [InlineData("email me", "email,offplatform")]
    [InlineData("proton mail", "email")]
    [InlineData("add me on WhatsApp", "platform,offplatform")]
    [InlineData("what's app me", "platform,offplatform")]
    [InlineData("whats app me", "platform,offplatform")]
    [InlineData("what sapp me", "platform,offplatform")]
    [InlineData("watsapp me", "platform,offplatform")]
    [InlineData("wats app me", "platform,offplatform")]
    [InlineData("my insta gram", "platform")]
    [InlineData("search my name on TikTok", "platform,offplatform")]
    [InlineData("search my name on tik tok", "platform,offplatform")]
    [InlineData("do you have insta", "platform,offplatform")]
    [InlineData("snap chat", "platform")]
    [InlineData("face book", "platform")]
    [InlineData("on signal", "platform")]
    [InlineData("facebook messenger", "platform")]
    [InlineData("message me on messenger", "phone,platform,offplatform")]
    [InlineData("we can meet on Zoom instead", "platform")]
    [InlineData("send a zoom link", "platform")]
    [InlineData("we can talk via Google Meet", "platform")]
    [InlineData("we can meet through FaceTime", "platform")]
    [InlineData("follow my YouTube channel", "platform")]
    [InlineData("I am on Threads", "platform")]
    [InlineData("join me on Discord", "platform")]
    [InlineData("I teach on Preply", "platform")]
    [InlineData("find me on italki", "platform,offplatform")]
    [InlineData("contact me directly", "offplatform")]
    [InlineData("DM me", "offplatform")]
    [InlineData("how can I contact you after the class", "offplatform")]
    [InlineData("where could I get hold of you", "offplatform")]
    [InlineData("what's your username", "offplatform")]
    [InlineData("share your contact details", "offplatform")]
    [InlineData("look me up", "offplatform")]
    [InlineData("follow me on my account", "offplatform")]
    [InlineData("accept my friend request", "offplatform")]
    [InlineData("let's continue the lessons privately", "offplatform")]
    [InlineData("let’s continue the lessons privately", "offplatform")]
    [InlineData("we could have lessons separately", "offplatform")]
    [InlineData("I teach private classes on my own website", "offplatform")]
    [InlineData("let's continue without the academy", "offplatform")]
    [InlineData("we can do this off-platform", "offplatform")]
    [InlineData("quit the subscription", "offplatform")]
    [InlineData("I will tutor you personally", "offplatform")]
    [InlineData("tell your dad to call me", "offplatform")]
    [InlineData("tell your dad to reach out to me directly", "offplatform")]
    [InlineData("get in touch with me after class", "offplatform")]
    [InlineData("ask your guardian to contact me", "offplatform")]
    [InlineData("give your mother my details", "offplatform")]
    [InlineData("what is your father's number", "offplatform")]
    [InlineData("your mother’s email", "offplatform")]
    [InlineData("you can pay me directly by Vodafone Cash, it's half the price", "payment")]
    [InlineData("the academy takes too much, we can do it cheaper", "payment")]
    [InlineData("transfer it to me personally", "payment")]
    [InlineData("send the money to me instead", "payment")]
    [InlineData("I can offer a lower rate", "payment")]
    [InlineData("half the price", "payment")]
    [InlineData("we could do it for less", "payment")]
    [InlineData("we could do cheaper lessons, just us", "payment")]
    [InlineData("without the middleman", "payment")]
    [InlineData("PayPal", "payment")]
    [InlineData("InstaPay", "payment")]
    [InlineData("bank transfer", "payment")]
    [InlineData("cash app", "payment")]
    [InlineData("wise transfer", "payment")]
    [InlineData("western union", "payment")]
    [InlineData("USDT", "payment")]
    [InlineData("don't tell your parents about this", "secrecy")]
    [InlineData("do not mention it to the academy", "secrecy")]
    [InlineData("dont tell anyone", "secrecy")]
    [InlineData("don't let the academy know", "secrecy")]
    [InlineData("never let your parents know", "secrecy")]
    [InlineData("keep this between us", "secrecy")]
    [InlineData("let's keep it a secret", "secrecy")]
    [InlineData("our little secret", "secrecy")]
    [InlineData("between you and me", "secrecy")]
    [InlineData("they are recording, I'll tell you later", "secrecy")]
    [InlineData("they’re recording", "secrecy")]
    [InlineData("the academy is monitoring", "secrecy")]
    [InlineData("delete our messages", "secrecy")]
    [InlineData("turn off the mic so I tell you", "secrecy")]
    public void FlagsContactPaymentAndSecrecyLanguage(string text, string expected)
    {
        var flags = TranscriptFlags.Scan(text);

        foreach (var code in expected.Split(',')) Assert.Contains(code, flags);
        Assert.Equal(flags.Count, flags.Distinct().Count());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n ")]
    [InlineData("read page number twelve")]
    [InlineData("what number is this ayah")]
    [InlineData("surah number one hundred and twelve")]
    [InlineData("count the number of verses")]
    [InlineData("number the letters")]
    [InlineData("what is the number of this surah")]
    [InlineData("peace be upon the Messenger of Allah")]
    [InlineData("read with tarteel")]
    [InlineData("Surah Al-Bayyinah")]
    [InlineData("we are studying Quranic Arabic")]
    [InlineData("after the lesson, revise these verses")]
    [InlineData("let's continue after the break")]
    [InlineData("we can continue without the mushaf")]
    [InlineData("zoom in on the page")]
    [InlineData("my page is twelve")]
    [InlineData("share your page")]
    [InlineData("follow me, repeat after me")]
    [InlineData("follow me slowly")]
    [InlineData("share your screen")]
    [InlineData("share the page")]
    [InlineData("the point of contact for this letter is the throat")]
    [InlineData("we reached the end of the surah")]
    [InlineData("let's reach ayah ten")]
    [InlineData("call me teacher")]
    [InlineData("call me ustadha")]
    [InlineData("call me")]
    [InlineData("read the Arabic text")]
    [InlineData("the call to prayer")]
    [InlineData("the time is ten thirty to eleven thirty")]
    [InlineData("see you next lesson on Tuesday")]
    [InlineData("message from the academy")]
    [InlineData("one to one lessons")]
    [InlineData("book extra classes")]
    [InlineData("the internet signal is weak")]
    [InlineData("these threads hold the book together")]
    [InlineData("2026-11-01")]
    [InlineData("2026-11-01 10:30")]
    [InlineData("01/11/2026 10:30 to 11:30")]
    [InlineData("2026/11/01 10:30:15")]
    [InlineData("10:30 to 11:30")]
    [InlineData("12345678")]
    [InlineData("+1234567")]
    [InlineData("one two three four five six seven eight")]
    [InlineData("plus one two three four five six seven")]
    public void DoesNotFlagOrdinaryLessonLanguage(string text)
    {
        Assert.Empty(TranscriptFlags.Scan(text));
    }

    [Fact]
    public void ReturnsEachCategoryOnceInStableOrder()
    {
        var flags = TranscriptFlags.Scan("my number is 123456789; email me at john@example.org; add me on WhatsApp; pay me; keep this between us");

        Assert.Equal(new[] { "phone", "email", "platform", "offplatform", "payment", "secrecy" }, flags);
    }

    [Theory]
    [InlineData("word ")]
    [InlineData("one ")]
    [InlineData("a at a dot ")]
    [InlineData("don't let ")]
    public void HandlesMaximumLengthContext(string unit)
    {
        var text = string.Concat(Enumerable.Repeat(unit, 8000 / unit.Length));

        var flags = TranscriptFlags.Scan(text);

        Assert.Equal(flags.Count, flags.Distinct().Count());
    }

    [Fact]
    public void MatchingIsIndependentOfServerCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.Contains("platform", TranscriptFlags.Scan("INSTAGRAM"));
            Assert.Contains("email", TranscriptFlags.Scan("SHARE YOUR EMAIL"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
