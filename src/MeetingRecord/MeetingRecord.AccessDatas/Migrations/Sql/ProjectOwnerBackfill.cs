namespace MeetingRecord.AccessDatas.Migrations.Sql;

/// <summary>
/// 1.0.52 migration <c>AddProjectOwnerUser</c> 的回填 SQL：把升級前手打的負責人文字（<c>Project.Owner</c>）
/// 對應到帳號，寫進新欄位 <c>Project.OwnerUserId</c>。抽成常數是為了讓 <c>ProjectOwnerBackfillSqlTests</c> 直接驗證。
///
/// <para>
/// 規則：去頭尾空白後與帳號的姓名（<c>Name</c>）或帳號（<c>Account</c>）完全相同，而且<b>剛好只對到一個帳號</b>才填；
/// 對不到或對到好幾個就留 null（不猜），畫面顯示「未指定（原：XXX）」，下次編輯時再選。
/// </para>
/// </summary>
public static class ProjectOwnerBackfill
{
    public const string Sql = """
        UPDATE "Project" SET "OwnerUserId" = (
            SELECT MIN(u."Id") FROM "MyUser" AS u
            WHERE u."Name" = TRIM("Project"."Owner") OR u."Account" = TRIM("Project"."Owner"))
        WHERE "OwnerUserId" IS NULL
          AND TRIM("Owner") <> ''
          AND (SELECT COUNT(*) FROM "MyUser" AS u
               WHERE u."Name" = TRIM("Project"."Owner") OR u."Account" = TRIM("Project"."Owner")) = 1;
        """;
}
