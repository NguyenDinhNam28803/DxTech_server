namespace server.Modules.Content.Domain;

/// <summary>Loại nội dung (ADR-015 §2). Lưu DB: news | solution | training | resource | glossary | page.</summary>
internal enum PostType
{
    News,
    Solution,
    Training,
    Resource,
    Glossary,
    Page,
}

/// <summary>
/// draft | published | archived. Bài hiển thị công khai khi <c>status = 'published' AND published_at &lt;= now()</c>
/// ⇒ <c>published_at</c> ở tương lai là hẹn giờ, không cần job.
/// </summary>
internal enum PostStatus
{
    Draft,
    Published,
    Archived,
}

/// <summary>Hình thức khoá học: online | offline | hybrid.</summary>
internal enum CourseFormat
{
    Online,
    Offline,
    Hybrid,
}

/// <summary>Trạng thái lịch khai giảng: open | full | closed.</summary>
internal enum CourseSessionStatus
{
    Open,
    Full,
    Closed,
}

/// <summary>Loại đăng ký: consultation (tư vấn) | course (khoá học).</summary>
internal enum LeadKind
{
    Consultation,
    Course,
}

/// <summary>Trạng thái xử lý lead: new | contacted | closed.</summary>
internal enum LeadStatus
{
    New,
    Contacted,
    Closed,
}
