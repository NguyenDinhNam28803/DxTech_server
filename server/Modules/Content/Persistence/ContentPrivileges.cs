namespace server.Modules.Content.Persistence;

/// <summary>
/// Thu hồi quyền thừa của role <c>dxtechs_app</c> trên schema <c>content</c> (thiết kế CT mục 1.3, ADR-009):
/// bài, chủ đề, ảnh — xoá mềm nên không DELETE; revision chỉ thêm (DELETE giữ cho job dọn bản cũ, không UPDATE);
/// lead không DELETE (ẩn danh hoá bằng UPDATE). Chạy ở migration (lần tạo bảng) VÀ ở <c>deploy/postgres/roles.sql</c> mục 5c
/// (mỗi lần deploy, vì mục 5 cấp lại quyền cho mọi bảng). Hai bản phải giống hệt — test so sánh.
/// </summary>
internal static class ContentPrivileges
{
    public const string Sql = """
        DO $content$
        BEGIN
          IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'dxtechs_app') THEN
            REVOKE DELETE, TRUNCATE ON content.posts, content.topics, content.media, content.leads FROM dxtechs_app;
            REVOKE UPDATE, TRUNCATE ON content.post_revisions FROM dxtechs_app;
            REVOKE TRUNCATE ON content.slug_redirects, content.course_sessions FROM dxtechs_app;
          END IF;
        END
        $content$;
        """;
}
