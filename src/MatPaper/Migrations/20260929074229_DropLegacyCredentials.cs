using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MatPaper.Migrations
{
    /// <summary>
    /// Drops the auth-only <c>Credential</c> table and the inline SMB columns of
    /// <c>StorageLocation</c>, which <c>Connection</c> replaced. Before dropping anything, it folds
    /// whatever still depends on them forward, so an instance upgrading straight from a
    /// pre-connection release loses nothing:
    /// <list type="bullet">
    /// <item>An SMB storage location without a connection gets one, built from its inline host/share
    /// and the credential it referenced; its sub-folder moves to <c>BasePath</c>. Connection names
    /// are kept unique (live locations first), since a deleted and a live location may share one.</item>
    /// <item>A mail/SMB import task that still references a credential gets that login copied into
    /// its own inline fields, and the dead <c>CredentialId</c> is removed from its settings. This
    /// happens even when the task also has a connection: the connection wins while it resolves, and
    /// if it is gone the task falls back to the same login it used before.</item>
    /// </list>
    /// Soft-deleted credentials are ignored, exactly as every runtime lookup ignored them, so a task
    /// pointing at one keeps its own inline login. Protected secrets are copied verbatim (same
    /// DataProtection purpose on both sides). Settings that are not readable JSON are left untouched.
    /// <see cref="Down"/> restores the schema only, not the data.
    /// </summary>
    public partial class DropLegacyCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    loc  RECORD;
                    t    RECORD;
                    cred RECORD;
                    s    jsonb;
                    v_id bigint;
                    v_name text;
                    n    int;
                BEGIN
                    -- 1) Legacy SMB storage locations -> a Connection each. UpdateState 0 = Deleted.
                    FOR loc IN
                        SELECT l."Id", l."Name", l."SmbHost", l."SmbShare", l."SmbPath",
                               c."Username" AS cred_user, c."Domain" AS cred_domain,
                               c."ProtectedPassword" AS cred_secret
                        FROM "StorageLocation" l
                        LEFT JOIN "Credential" c ON c."Id" = l."CredentialId" AND c."UpdateState" <> 0
                        WHERE l."Kind" = 1 AND l."ConnectionId" IS NULL
                        ORDER BY (l."UpdateState" = 0), l."Id"
                    LOOP
                        -- Live locations go first and keep their name; later ones get " (2)", ...
                        v_name := loc."Name";
                        n := 2;
                        WHILE EXISTS (SELECT 1 FROM "Connection"
                                      WHERE lower("Name") = lower(v_name) AND "UpdateState" <> 0) LOOP
                            v_name := loc."Name" || ' (' || n || ')';
                            n := n + 1;
                        END LOOP;

                        INSERT INTO "Connection" (
                            "Name", "Kind", "AuthMode", "Provider", "SettingsJson",
                            "Username", "Domain", "ProtectedPassword",
                            "ProtectedClientSecret", "ProtectedRefreshToken",
                            "UpdateState", "CreateDate", "UpdateDate")
                        VALUES (
                            v_name, 0, 0, 0,
                            jsonb_build_object(
                                'Host',  COALESCE(loc."SmbHost", ''),
                                'Share', COALESCE(loc."SmbShare", ''))::text,
                            COALESCE(loc.cred_user, ''), loc.cred_domain, COALESCE(loc.cred_secret, ''),
                            '', '',
                            1, now(), now())
                        RETURNING "Id" INTO v_id;

                        UPDATE "StorageLocation"
                        SET "ConnectionId" = v_id,
                            "BasePath"     = loc."SmbPath",
                            "UpdateDate"   = now()
                        WHERE "Id" = loc."Id";
                    END LOOP;

                    -- 2) Mail/SMB import tasks referencing a credential -> inline login.
                    FOR t IN
                        SELECT "Id", "Type", "SettingsJson"
                        FROM "ImportTask"
                        WHERE "Type" IN (0, 1, 3) AND "SettingsJson" LIKE '%CredentialId%'
                    LOOP
                        BEGIN
                            s := t."SettingsJson"::jsonb;
                        EXCEPTION WHEN others THEN
                            CONTINUE; -- unreadable settings: leave the row alone
                        END;
                        IF jsonb_typeof(s) IS DISTINCT FROM 'object' THEN
                            CONTINUE;
                        END IF;

                        -- A live credential overrode the inline login at runtime, so it becomes the
                        -- inline login. Also for tasks on a connection: the connection still wins
                        -- while it resolves; if it is deleted, the task keeps working as before.
                        -- A deleted credential was ignored at runtime, so the inline login stays.
                        IF jsonb_typeof(s -> 'CredentialId') = 'number' THEN
                            SELECT "Username", "Domain", "ProtectedPassword" INTO cred
                            FROM "Credential"
                            WHERE "Id" = (s ->> 'CredentialId')::bigint AND "UpdateState" <> 0;

                            IF FOUND THEN
                                s := s || jsonb_build_object(
                                    'Username',          cred."Username",
                                    'ProtectedPassword', cred."ProtectedPassword");
                                IF t."Type" = 3 THEN
                                    s := s || jsonb_build_object('Domain', cred."Domain");
                                END IF;
                            END IF;
                        END IF;

                        UPDATE "ImportTask"
                        SET "SettingsJson" = (s - 'CredentialId')::text,
                            "UpdateDate"   = now()
                        WHERE "Id" = t."Id";
                    END LOOP;
                END $$;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_StorageLocation_Credential_CredentialId",
                table: "StorageLocation");

            migrationBuilder.DropTable(
                name: "Credential");

            migrationBuilder.DropIndex(
                name: "IX_StorageLocation_CredentialId",
                table: "StorageLocation");

            migrationBuilder.DropColumn(
                name: "CredentialId",
                table: "StorageLocation");

            migrationBuilder.DropColumn(
                name: "SmbHost",
                table: "StorageLocation");

            migrationBuilder.DropColumn(
                name: "SmbPath",
                table: "StorageLocation");

            migrationBuilder.DropColumn(
                name: "SmbShare",
                table: "StorageLocation");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CredentialId",
                table: "StorageLocation",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SmbHost",
                table: "StorageLocation",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SmbPath",
                table: "StorageLocation",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SmbShare",
                table: "StorageLocation",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Credential",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateUserId = table.Column<long>(type: "bigint", nullable: true),
                    Domain = table.Column<string>(type: "text", nullable: true),
                    Name = table.Column<string>(type: "text", nullable: false),
                    ProtectedPassword = table.Column<string>(type: "text", nullable: false),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdateState = table.Column<int>(type: "integer", nullable: false),
                    UpdateUserId = table.Column<long>(type: "bigint", nullable: true),
                    Username = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Credential", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StorageLocation_CredentialId",
                table: "StorageLocation",
                column: "CredentialId");

            migrationBuilder.AddForeignKey(
                name: "FK_StorageLocation_Credential_CredentialId",
                table: "StorageLocation",
                column: "CredentialId",
                principalTable: "Credential",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
