using Microsoft.EntityFrameworkCore;
using MeetingRecord.AccessDatas.Models;

namespace MeetingRecord.AccessDatas;

public partial class BackendDBContext : DbContext
{
    public BackendDBContext()
    {
    }

    public BackendDBContext(DbContextOptions<BackendDBContext> options)
    : base(options)
    {
    }

    public virtual DbSet<MyUser> MyUser { get; set; }
    public virtual DbSet<Project> Project { get; set; }
    public virtual DbSet<ProjectFile> ProjectFile { get; set; }
    public virtual DbSet<RoleView> RoleView { get; set; }
    public virtual DbSet<Category> Category { get; set; }
    public virtual DbSet<Team> Team { get; set; }
    public virtual DbSet<PromptTemplate> PromptTemplate { get; set; }
    public virtual DbSet<Meeting> Meeting { get; set; }
    public virtual DbSet<Todo> Todo { get; set; }
    public virtual DbSet<AuditLog> AuditLog { get; set; }
    public virtual DbSet<Permission> Permission { get; set; }
    public virtual DbSet<RolePermissionMap> RolePermissionMap { get; set; }
    public virtual DbSet<UserRole> UserRole { get; set; }
    public virtual DbSet<UserTeam> UserTeam { get; set; }

    /// <summary>專案的主責與協作團隊（0.4.102），專案可見範圍的唯一來源。每個專案恰好一筆主責，沒有公開專案。</summary>
    public virtual DbSet<ProjectTeam> ProjectTeam { get; set; }
    public virtual DbSet<CategoryTeam> CategoryTeam { get; set; }

    /// <summary>AI 呼叫的用量帳本（0.4.80）。一次呼叫一列，永久保留，不回填歷史。</summary>
    public virtual DbSet<AiUsageLog> AiUsageLog { get; set; }

    /// <summary>系統例外紀錄（0.4.122）。相同簽章合併成一列並累加次數，完整堆疊存在 ExceptionPath 的檔案裡。</summary>
    public virtual DbSet<ExceptionLog> ExceptionLog { get; set; }

    /// <summary>AI 對話紀錄（1.0.29）。中繼資料一列一次呼叫，送出內容與回應存在 AiCallLogPath 的檔案裡。</summary>
    public virtual DbSet<AiCallLog> AiCallLog { get; set; }

    /// <summary>待辦的異動紀錄（1.0.44）。一個欄位一列，給專案成員看「誰把什麼改了」。</summary>
    public virtual DbSet<TodoChange> TodoChange { get; set; }

    /// <summary>站內通知（1.0.44），右上角鈴鐺。</summary>
    public virtual DbSet<UserNotification> UserNotification { get; set; }


    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            // 連線設定一律由 MeetingRecord.Web 的 AddConfiguredDatabase 以 SQLite 註冊。
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.UseCollation("Chinese_Taiwan_Stroke_CI_AS");

        #region 設定階層級的刪除政策(預設若關聯子資料表有紀錄，父資料表不可強制刪除
        foreach (var relationship in modelBuilder.Model.GetEntityTypes().SelectMany(e => e.GetForeignKeys()))
        {
            relationship.DeleteBehavior = DeleteBehavior.Restrict;
        }
        #endregion

        modelBuilder.Entity<Project>(entity =>
        {
            entity.HasMany(x => x.Files)
                .WithOne(x => x.Project)
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            // 會議紀錄不是專案的附件：刪專案只解除歸屬，逐字稿與 AI 草稿保留。
            // 上面的迴圈已把所有外部索引鍵預設為 Restrict，因此這裡必須明寫 SetNull。
            entity.HasMany(x => x.Meetings)
                .WithOne(x => x.Project)
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.SetNull);

            // 待辦與會議紀錄不同：沒有專案的待辦沒有意義，跟著專案一起刪。
            entity.HasMany(x => x.Todos)
                .WithOne(x => x.Project)
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProjectTeam>(entity =>
        {
            entity.HasIndex(x => new { x.ProjectId, x.TeamId }).IsUnique();
            entity.HasOne(x => x.Project).WithMany(x => x.Teams).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Team).WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CategoryTeam>(entity =>
        {
            entity.HasIndex(x => new { x.CategoryId, x.TeamId }).IsUnique();
            entity.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Team).WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Meeting>(entity =>
        {
            // 待辦可回溯到來源會議紀錄；會議紀錄被刪除時待辦保留，只是失去來源。
            entity.HasMany(x => x.Todos)
                .WithOne(x => x.Meeting)
                .HasForeignKey(x => x.MeetingId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        #region RBAC 關聯（多對多）與唯一鍵
        modelBuilder.Entity<Permission>(entity =>
        {
            entity.HasIndex(x => x.Key).IsUnique();
        });

        modelBuilder.Entity<RolePermissionMap>(entity =>
        {
            entity.HasIndex(x => new { x.RoleViewId, x.PermissionId }).IsUnique();
            entity.HasOne(x => x.RoleView).WithMany().HasForeignKey(x => x.RoleViewId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Permission).WithMany().HasForeignKey(x => x.PermissionId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserRole>(entity =>
        {
            entity.HasIndex(x => new { x.MyUserId, x.RoleViewId }).IsUnique();
            entity.HasOne(x => x.MyUser).WithMany().HasForeignKey(x => x.MyUserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.RoleView).WithMany().HasForeignKey(x => x.RoleViewId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserTeam>(entity =>
        {
            entity.HasIndex(x => new { x.MyUserId, x.TeamId }).IsUnique();
            entity.HasOne(x => x.MyUser).WithMany().HasForeignKey(x => x.MyUserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Team).WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Cascade);
        });
        #endregion

        modelBuilder.Entity<AiUsageLog>(entity =>
        {
            // 每一個查詢都以時間範圍開頭（本月、上月同期、最近 N 天、明細分頁），
            // 分組則全部在記憶體做（資料量與 DashboardService 同一個量級）。
            // 刻意不為 Feature／UserId 另建索引：它們只在日期過濾之後才用到，
            // 選擇性不足以打敗表掃描，徒增寫入成本——而帳本是全系統寫入最頻繁的表。
            entity.HasIndex(x => x.OccurredAt);

            // 1.0.29：從 AI 對話紀錄的明細找回同一次呼叫的用量。
            entity.HasIndex(x => x.CallId);
        });

        modelBuilder.Entity<AiCallLog>(entity =>
        {
            entity.HasIndex(x => x.CallId).IsUnique();
            entity.HasIndex(x => x.OccurredAt);

            // 刪除會議／專案時連帶刪除，以及明細窗列出同一趟呼叫。
            entity.HasIndex(x => x.MeetingId);
            entity.HasIndex(x => x.ProjectId);
            entity.HasIndex(x => x.ThreadKey);

            entity.Property(x => x.Provider).HasMaxLength(100);
            entity.Property(x => x.Model).HasMaxLength(200);
            entity.Property(x => x.UserName).HasMaxLength(200);
            entity.Property(x => x.RelatedInfo).HasMaxLength(500);
            entity.Property(x => x.ThreadKey).HasMaxLength(100);
            entity.Property(x => x.ErrorMessage).HasMaxLength(500);
            entity.Property(x => x.ContentFile).HasMaxLength(100);
        });

        modelBuilder.Entity<AuditLog>(entity =>
        {
            // 1.0.32 稽核紀錄頁：預設排序（新到舊）、時間區間與清除門檻都吃 OccurredAt；
            // 動作下拉與篩選吃 Action；「某人的紀錄」吃 (ActorUserId, OccurredAt)。
            entity.HasIndex(x => x.OccurredAt);
            entity.HasIndex(x => x.Action);
            entity.HasIndex(x => new { x.ActorUserId, x.OccurredAt });

            entity.Property(x => x.ClientIp).HasMaxLength(64);
        });

        modelBuilder.Entity<ExceptionLog>(entity =>
        {
            // 合併的唯一依據。寫入已由單一消費者序列化，這個索引是第二道防線。
            entity.HasIndex(x => x.Signature).IsUnique();

            // 預設排序（最後發生 desc）與「清除 N 天未再發生」都吃這個索引。
            entity.HasIndex(x => x.LastOccurredAt);
        });

        modelBuilder.Entity<Todo>(entity =>
        {
            // 子項目（1.0.45）：刪主待辦連子項目一起刪；全站預設 Restrict，這裡要明寫。
            entity.HasOne(x => x.Parent)
                .WithMany(x => x.Children)
                .HasForeignKey(x => x.ParentTodoId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TodoChange>(entity =>
        {
            // 異動紀錄視窗（某筆待辦、新到舊）與清單的「期限已變更」子查詢（最近一筆 DueDate 異動）。
            entity.HasIndex(x => new { x.TodoId, x.ChangedAt });
            entity.HasIndex(x => new { x.Field, x.ChangedAt });

            entity.Property(x => x.Field).HasMaxLength(20);
            entity.Property(x => x.OldValue).HasMaxLength(200);
            entity.Property(x => x.NewValue).HasMaxLength(200);
            entity.Property(x => x.ChangedByName).HasMaxLength(100);
            entity.Property(x => x.Via).HasMaxLength(20);
            entity.Property(x => x.SourceMeetingTitle).HasMaxLength(200);

            // 待辦刪了，它的異動紀錄就沒有意義；全站預設 Restrict，這裡要明寫，否則刪待辦會失敗。
            entity.HasOne(x => x.Todo)
                .WithMany()
                .HasForeignKey(x => x.TodoId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserNotification>(entity =>
        {
            // 鈴鐺：某人的未讀數與最近 N 則都從 UserId 開頭。
            entity.HasIndex(x => new { x.UserId, x.ReadAt, x.CreatedAt });
            entity.HasIndex(x => x.CreatedAt);

            entity.Property(x => x.Message).HasMaxLength(500);

            entity.HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
