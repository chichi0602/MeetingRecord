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

    public virtual DbSet<AiChatMessage> AiChatMessage { get; set; }

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

        modelBuilder.Entity<Meeting>(entity =>
        {
            // 待辦可回溯到來源會議紀錄；會議紀錄被刪除時待辦保留，只是失去來源。
            entity.HasMany(x => x.Todos)
                .WithOne(x => x.Meeting)
                .HasForeignKey(x => x.MeetingId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<AiChatMessage>(entity =>
        {
            // 對話依附於它問的那個對象，對象沒了對話也就沒有意義，兩邊都是 Cascade。
            entity.HasOne(x => x.Project).WithMany()
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(x => x.Meeting).WithMany()
                .HasForeignKey(x => x.MeetingId)
                .OnDelete(DeleteBehavior.Cascade);

            // 讀取一定是「某個對象的整段對話依時間排序」，兩個索引各服務一種範圍。
            entity.HasIndex(x => new { x.ProjectId, x.Id });
            entity.HasIndex(x => new { x.MeetingId, x.Id });
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

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
