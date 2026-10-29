using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Oscova.Agents.Infrastructure.Migrations
{
    /// <summary>Turns on the new shared folder permission for every existing non-operator user except the synthetic system account.</summary>
    public partial class GrantSharedFolderPermission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The values stay literal so the migration keeps meaning the same thing if the keys are renamed later.
            migrationBuilder.Sql("""
                INSERT INTO "AspNetUserClaims" ("UserId", "ClaimType", "ClaimValue")
                SELECT u."Id", 'oscova.permission', 'shared-folder'
                FROM "AspNetUsers" u
                WHERE u."Id" <> '__system__'
                AND NOT EXISTS (
                    SELECT 1 FROM "AspNetUserRoles" ur
                    JOIN "AspNetRoles" r ON r."Id" = ur."RoleId"
                    WHERE ur."UserId" = u."Id" AND r."NormalizedName" = 'ADMIN')
                AND NOT EXISTS (
                    SELECT 1 FROM "AspNetUserClaims" c
                    WHERE c."UserId" = u."Id" AND c."ClaimType" = 'oscova.permission' AND c."ClaimValue" = 'shared-folder');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM "AspNetUserClaims"
                WHERE "ClaimType" = 'oscova.permission' AND "ClaimValue" = 'shared-folder';
                """);
        }
    }
}
