using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Academy.Api;

/// <summary>
/// The documents an account has to accept, clause by clause, before it can start
/// working. Not the lesson-ops <see cref="AcademyPolicy"/> row: that one holds the
/// rule numbers, this one holds the agreement text.
///
/// A rule number is never typed into the text. The clause carries a token such as
/// <c>{freeCancelHours}</c> and <see cref="Render"/> fills it from the live policy
/// row, so a signed agreement cannot go on promising a window the engine no longer
/// applies. The version is a fingerprint of the filled text, so moving a number on
/// the admin policy desk changes the document, which makes every earlier signature
/// stale and re-prompts that account on its next gated action.
/// </summary>
public static class PolicyKeys
{
    public const string Teacher = "teacher";
    public const string Parent = "parent";
}

public sealed record PolicyClause(string Id, string Title, string Body);

public sealed record PolicyDefinition(
    string Key,
    int Version,
    string Language,
    string Title,
    string Summary,
    IReadOnlyList<PolicyClause> Clauses)
{
    public IReadOnlyList<string> ClauseIds { get; } = [.. Clauses.Select(x => x.Id)];
}

public static class PolicyCatalog
{
    /// <summary>Template text. Only <see cref="Render"/> hands a document out, and it sets the version.</summary>
    private static readonly PolicyDefinition Teacher = new(
        PolicyKeys.Teacher,
        0,
        "ar",
        "لوائح معلمي أكاديمية أنا قرآن",
        "القواعد التي يعمل بها كل معلّم في الأكاديمية. تُقرأ وتُقبل بندًا بندًا قبل أول إجراء.",
        [
            new("language", "لغة التدريس",
                """
                كل الحصص تُقدَّم باللغة الإنجليزية: القرآن، والتجويد، والدراسات الإسلامية، والعربية.
                الطالب يُدرَس بالإنجليزية وفي منطقته الزمنية، فلا تشرح بلغة لا يتابعها الطالب،
                ولا تُبدّل لغة الحصة باتفاق شخصي معه.
                """),
            new("assignment", "إسناد الطلاب",
                """
                الأكاديمية هي التي تسند الطالب إلى معلّم، ولكل طالب معلّم واحد نشِط في الوقت نفسه.
                إذا نُقل طالب منك إلى معلّم آخر يصلك إشعار في التطبيق وبريد إلكتروني.
                لا تتواصل مع طالب لم يُسند إليك، ولا تطلب من طالب مُسند إليك أن يطلب تغيير معلّمه.
                """),
            new("weekly-times", "الأوقات الأسبوعية",
                """
                انشر الأوقات التي تستطيع الالتزام بها كل أسبوع. نشر الوقت يعني موافقتك على الحجز فيه،
                ويستطيع الطالب نقل حصته الأسبوعية إلى وقت آخر نشرته أنت فيصلك إشعار بالوقت الجديد
                دون حاجة إلى موافقتك. ألغِ من جدولك الوقت الذي لا تستطيع الالتزام به بدلًا من ترك طلاب فيه.
                """),
            new("cancellation", "الإلغاء والغياب",
                """
                للطالب أن يلغي الحصة مجانًا قبل موعدها بما لا يقل عن {freeCancelHours} ساعة، ويلزمك
                أنت إخطار لا يقل عن {teacherNoticeHours} ساعة. ما دون ذلك يُسجَّل إلغاءً متأخرًا على
                من ألغى، وأجر الحصة يبقى لك إذا ألغى الطالب متأخرًا. تُعدّ الحصة غيابًا إذا لم يحضر
                الطرف الآخر خلال {noShowGraceMinutes} دقائق من الموعد. التكرار يُراجعه المشرفون وقد
                يوقف حجز حصص جديدة حتى تتواصل مع الإدارة.
                أبلغ الإدارة فورًا بأي طارئ، وكل حصة يلزمها تقرير بعد انتهائها.
                """),
            new("classroom", "غرفة الصف",
                """
                الحصص تُقام في غرفة الصف الخاصة بالأكاديمية وتُفتح من حسابك قبل الموعد بدقائق.
                ادخل من مكان هادئ جيد الإضاءة بكاميرا وميكروفون يعملين واتصال إنترنت مستقر.
                لا تنقل الحصة إلى زوم أو واتساب أو أي تطبيق آخر، ولا تشارك رابط الغرفة مع أي شخص
                خارج الحصة، ولا تسجّل الحصة بنفسك.
                """),
            new("conduct", "السلوك وحماية الطلاب",
                """
                يبقى التعامل مع الطالب وأسرته داخل قنوات الأكاديمية الرسمية. لا تتبادل أرقام الهواتف
                أو حسابات التواصل الاجتماعي مع الطلاب، ولا تقبل هدايا أو أموالًا منهم أو من ذويهم،
                ولا تطلب شيئًا من ذلك. قد يكون بعض الطلاب قُصّرًا فيفترض حضور ولي الأمر ومتابعته،
                وإذا لاحظت ما يثير القلق على سلامة طالب فأبلِغ الإدارة فورًا.
                """),
            new("review", "المتابعة والجودة",
                """
                للأكاديمية حضور أي حصة ومراجعتها. قد تُحفَظ نصوص كُتبت أثناء الحصة — لا ملفات صوتية —
                ليقرؤها فريق الدعم ويقيّم جودة الشرح، وتُحذَف تلقائيًا بعد ثلاثين يومًا.
                التقرير الشهري أو تقرير كل حصة هو ما يُغلق الحصة في سجلات الأكاديمية، ولا يُقبل في ذلك بديل.
                """),
            new("payment", "الأجر والمستحقات",
                """
                الأجر يُحسب على الحصص الفعلية خلال الشهر، وتُرسل الأكاديمية كشفًا شهريًا بأسعار متفق عليها.
                تُصرف المستحقات بالتحويل إلى البيانات المالية التي تُدخلها في حسابك، والضرائب
                والالتزامات النظامية مسؤولية المعلم. لا تتقاضَّ أي مبلغ من الطالب أو ذويه مباشرة.
                """),
            new("updates", "تحديث اللوائح",
                """
                للأكاديمية تعديل هذه اللوائح. عند صدور نسخة جديدة تُعرض عليك وتُطلب موافقتك عليها قبل
                أي إجراء جديد في حسابك، ومتابعتك للتدريس بعد ذلك تعني قبولك للنسخة المحدَّثة.
                """),
        ]);

    /// <summary>Template text. Only <see cref="Render"/> hands a document out, and it sets the version.</summary>
    private static readonly PolicyDefinition Parent = new(
        PolicyKeys.Parent,
        0,
        "en",
        "Parent and Student Agreement",
        "What the academy promises and what it asks of the family, before the first lesson.",
        [
            new("language", "Everything is taught in English",
                "Quran, Tajweed, Islamic Studies and Arabic are all taught in English, in your own time zone. Please be sure the student can follow a lesson in English before you start."),
            new("assignment", "How a teacher is assigned",
                "You tell us the student's levels and the weekly times that suit you, and the academy then assigns one teacher and sets the weekly lessons. If the pairing is not working, tell the academy — do not rearrange it privately with the teacher."),
            new("schedule", "Weekly times and changes",
                "Each lesson is a recurring weekly time. You can move a weekly lesson to another time the same teacher has published; the teacher and the academy are told, and no approval is needed. You can end a weekly series yourself at any time."),
            new("cancellation", "Cancellations and no-shows",
                "You can cancel a lesson free of charge up to {freeCancelHours} hours before it starts, and your teacher is held to at least {teacherNoticeHours} hours' notice. A cancellation inside those windows is recorded against whoever cancelled. A lesson becomes a no-show if a participant has not joined within {noShowGraceMinutes} minutes of the start time. Repeated late cancellations or no-shows are reviewed by the academy, which may pause new bookings until you speak with us."),
            new("classroom", "The online classroom",
                "Lessons happen in the academy's own classroom, opened from your dashboard a few minutes before the start. Use a quiet, well-lit room with a working camera and microphone and a stable internet connection. Lessons are never moved to WhatsApp, Zoom or any other app, and the room link stays private to the teacher and the student."),
            new("minors", "If the student is under 18",
                "By ticking this you confirm you are the parent or guardian of the student, or an adult enrolling yourself. A guardian should know when lessons happen and where the student takes them. Tell the academy promptly about anything that worries you about a lesson or a teacher — you can reach us from the dashboard at any time."),
            new("records", "Reports, notes and quality review",
                "The teacher writes a short report after every lesson and you can read it on your schedule. To keep teaching quality up, the academy may review notes written during a lesson; those notes are read only by the academy's review team, they are not audio recordings, and they are deleted 30 days after the lesson."),
            new("payments", "Fees and invoices",
                "You choose a plan of 2, 3, 4 or 5 days a week. The academy sends an invoice for the lessons in each closed month, paid by card or bank transfer as agreed. There is no automatic renewal, and nothing is charged for a lesson that did not happen."),
            new("updates", "Changes to this agreement",
                "We may update this agreement. A new version is shown to you in the app and you are asked to accept it before the next lesson or schedule change; continuing with the academy afterwards means you accept the current version."),
        ]);

    private static readonly PolicyDefinition[] AllTemplates = [Teacher, Parent];

    /// <summary>The current document for this key, with the live rule numbers filled in.</summary>
    public static PolicyDefinition? Find(string? key, AcademyPolicy policy) =>
        Render(AllTemplates.FirstOrDefault(x =>
            string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase)), policy);

    /// <summary>The document an account of this role is asked to accept.</summary>
    public static PolicyDefinition? ForRole(string role, AcademyPolicy policy) => role switch
    {
        "teacher" => Render(Teacher, policy),
        "student" => Render(Parent, policy),
        _ => null,
    };

    private static PolicyDefinition? Render(PolicyDefinition? template, AcademyPolicy policy)
    {
        if (template is null) return null;

        var clauses = new List<PolicyClause>(template.Clauses.Count);
        var text = new StringBuilder(template.Key).Append('\n').Append(template.Language)
            .Append('\n').Append(template.Title).Append('\n').Append(template.Summary);
        foreach (var clause in template.Clauses)
        {
            var body = Fill(clause.Body, policy);
            clauses.Add(clause with { Body = body });
            text.Append('\n').Append(clause.Id).Append('\n').Append(clause.Title).Append('\n').Append(body);
        }

        return new PolicyDefinition(template.Key, VersionOf(text.ToString()),
            template.Language, template.Title, template.Summary, clauses);
    }

    private static int VersionOf(string rendered)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(rendered));
        return (hash[0] << 24 | hash[1] << 16 | hash[2] << 8 | hash[3]) & int.MaxValue;
    }

    private static string Fill(string body, AcademyPolicy policy) => body
        .Replace("{freeCancelHours}", Number(policy.StudentFreeCancelHours))
        .Replace("{teacherNoticeHours}", Number(policy.TeacherFreeCancelHours))
        .Replace("{noShowGraceMinutes}", Number(policy.NoShowGraceMinutes));

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
