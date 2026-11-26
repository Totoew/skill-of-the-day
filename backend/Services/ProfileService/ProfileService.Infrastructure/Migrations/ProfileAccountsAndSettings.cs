using FluentMigrator;

namespace ProfileService.Infrastructure.Migrations;

[Migration(20261125000100)]
public sealed class ProfileAccountsAndSettings : Migration
{
    public override void Up()
    {
        // Legacy numeric user IDs cannot be guessed from the new Auth UUIDs. Never discard existing data.
        Execute.Sql("""
            DO $$ BEGIN
              IF EXISTS (SELECT 1 FROM profile.user_profiles) OR EXISTS (SELECT 1 FROM profile.user_skills)
                  OR EXISTS (SELECT 1 FROM profile.user_regions) THEN
                RAISE EXCEPTION 'Legacy profile rows require an explicit numeric-user-ID to Auth UUID mapping before migration';
              END IF;
            END $$;
            ALTER TABLE profile.user_profiles ALTER COLUMN user_id TYPE uuid USING NULL::uuid;
            ALTER TABLE profile.user_skills ALTER COLUMN user_id TYPE uuid USING NULL::uuid;
            ALTER TABLE profile.user_regions ALTER COLUMN user_id TYPE uuid USING NULL::uuid;
            ALTER TABLE profile.user_profiles RENAME COLUMN direction_id TO direction;
            ALTER TABLE profile.user_profiles ALTER COLUMN direction TYPE text USING direction::text;
            ALTER TABLE profile.user_profiles ALTER COLUMN direction SET NOT NULL;
            ALTER TABLE profile.user_profiles ADD COLUMN level text NOT NULL DEFAULT 'all';
            ALTER TABLE profile.user_profiles ADD COLUMN version bigint NOT NULL DEFAULT 1 CHECK (version > 0);
            ALTER TABLE profile.user_profiles ADD COLUMN updated_at timestamptz NOT NULL DEFAULT now();
            ALTER TABLE profile.user_skills RENAME COLUMN skill_id TO skill_name;
            ALTER TABLE profile.user_skills ALTER COLUMN skill_name TYPE text USING skill_name::text;
            ALTER TABLE profile.user_skills ADD COLUMN position integer NOT NULL DEFAULT 0;
            ALTER TABLE profile.user_regions RENAME COLUMN region_id TO region;
            ALTER TABLE profile.user_regions ALTER COLUMN region TYPE text USING region::text;
            ALTER TABLE profile.user_regions ADD COLUMN position integer NOT NULL DEFAULT 0;
            ALTER TABLE profile.user_skills ADD CONSTRAINT fk_profile_skills_user FOREIGN KEY (user_id)
                REFERENCES profile.user_profiles(user_id) ON DELETE CASCADE;
            ALTER TABLE profile.user_regions ADD CONSTRAINT fk_profile_regions_user FOREIGN KEY (user_id)
                REFERENCES profile.user_profiles(user_id) ON DELETE CASCADE;
            """);
    }

    public override void Down() => throw new NotSupportedException("UUID profiles require a forward migration; restore a backup to return to legacy numeric accounts.");
}
