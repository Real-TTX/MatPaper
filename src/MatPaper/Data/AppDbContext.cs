using Microsoft.EntityFrameworkCore;

namespace MatPaper.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<Role> Roles => Set<Role>();
    public DbSet<User> Users => Set<User>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<StorageLocation> StorageLocations => Set<StorageLocation>();
    public DbSet<Correspondent> Correspondents => Set<Correspondent>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<DocumentType> DocumentTypes => Set<DocumentType>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocumentTag> DocumentTags => Set<DocumentTag>();
    public DbSet<DocumentShare> DocumentShares => Set<DocumentShare>();
    public DbSet<ImportTask> ImportTasks => Set<ImportTask>();
    public DbSet<ExportTask> ExportTasks => Set<ExportTask>();
    public DbSet<TaskRun> TaskRuns => Set<TaskRun>();
    public DbSet<ShareLink> ShareLinks => Set<ShareLink>();
    public DbSet<InboxItem> InboxItems => Set<InboxItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasPostgresExtension("pg_trgm");

        modelBuilder.Entity<Role>().ToTable("Role");
        modelBuilder.Entity<User>().ToTable("User");
        modelBuilder.Entity<UserSession>().ToTable("UserSession");
        modelBuilder.Entity<StorageLocation>().ToTable("StorageLocation");
        modelBuilder.Entity<Correspondent>().ToTable("Correspondent");
        modelBuilder.Entity<Tag>().ToTable("Tag");
        modelBuilder.Entity<Project>().ToTable("Project");
        modelBuilder.Entity<DocumentType>().ToTable("DocumentType");
        modelBuilder.Entity<Document>().ToTable("Document");
        modelBuilder.Entity<DocumentTag>().ToTable("DocumentTag");
        modelBuilder.Entity<DocumentShare>().ToTable("DocumentShare");
        modelBuilder.Entity<ImportTask>().ToTable("ImportTask");
        modelBuilder.Entity<ExportTask>().ToTable("ExportTask");
        modelBuilder.Entity<TaskRun>().ToTable("TaskRun");
        modelBuilder.Entity<ShareLink>().ToTable("ShareLink");
        modelBuilder.Entity<InboxItem>().ToTable("InboxItem");

        modelBuilder.Entity<User>()
            .HasIndex(u => u.Username)
            .IsUnique();

        modelBuilder.Entity<UserSession>()
            .HasIndex(s => s.Token)
            .IsUnique();

        modelBuilder.Entity<Document>()
            .HasIndex(d => d.Token)
            .IsUnique();

        modelBuilder.Entity<ShareLink>()
            .HasIndex(s => s.Token)
            .IsUnique();

        modelBuilder.Entity<DocumentTag>()
            .HasIndex(dt => new { dt.DocumentId, dt.TagId })
            .IsUnique();

        modelBuilder.Entity<Document>()
            .HasIndex(d => d.OwnerId);

        modelBuilder.Entity<Document>()
            .HasOne(d => d.Owner)
            .WithMany()
            .HasForeignKey(d => d.OwnerId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<Document>()
            .HasMany(d => d.Shares)
            .WithOne(s => s.Document!)
            .HasForeignKey(s => s.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<DocumentShare>()
            .HasOne(s => s.User)
            .WithMany()
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<DocumentShare>()
            .HasIndex(s => new { s.DocumentId, s.UserId })
            .IsUnique();

        modelBuilder.Entity<InboxItem>()
            .HasIndex(i => new { i.StorageLocationId, i.RelativePath })
            .IsUnique();

        modelBuilder.Entity<Document>(entity =>
        {
            entity.HasGeneratedTsVectorColumn(
                    d => d.SearchVector!,
                    "german",
                    d => new { d.Title, d.OcrText })
                .HasIndex(d => d.SearchVector)
                .HasMethod("GIN");
        });

        var seedTimestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        modelBuilder.Entity<Role>().HasData(
            new Role { Id = 1, Name = "Admin", CreateDate = seedTimestamp, CreateUserId = null, UpdateDate = seedTimestamp, UpdateUserId = null },
            new Role { Id = 2, Name = "User", CreateDate = seedTimestamp, CreateUserId = null, UpdateDate = seedTimestamp, UpdateUserId = null },
            new Role { Id = 3, Name = "Anonymous", CreateDate = seedTimestamp, CreateUserId = null, UpdateDate = seedTimestamp, UpdateUserId = null });

        var documentTypeNames = new[]
        {
            "Rechnung", "Angebot", "Anfrage", "Auftragsbestätigung", "Lieferschein",
            "Vertrag", "Mahnung", "Kontoauszug", "Bescheid", "Sonstiges"
        };

        var documentTypes = new DocumentType[documentTypeNames.Length];
        for (var i = 0; i < documentTypeNames.Length; i++)
        {
            documentTypes[i] = new DocumentType
            {
                Id = i + 1,
                Name = documentTypeNames[i],
                UpdateState = UpdateState.Created,
                CreateDate = seedTimestamp,
                CreateUserId = null,
                UpdateDate = seedTimestamp,
                UpdateUserId = null
            };
        }

        modelBuilder.Entity<DocumentType>().HasData(documentTypes);
    }
}
