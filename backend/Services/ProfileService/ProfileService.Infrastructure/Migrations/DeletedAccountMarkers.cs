using FluentMigrator;

namespace ProfileService.Infrastructure.Migrations;

[Migration(20261125000200)]
public sealed class DeletedAccountMarkers : Migration
{
    public override void Up() => Execute.Sql("CREATE TABLE profile.deleted_accounts (user_id uuid PRIMARY KEY, deleted_at timestamptz NOT NULL DEFAULT now());");
    public override void Down() => Delete.Table("deleted_accounts").InSchema("profile");
}
