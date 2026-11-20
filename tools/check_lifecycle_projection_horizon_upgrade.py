"""Qualify the historical additive horizon migration on its actual schema."""
from check_reference_editing import main
from check_lifecycle_projection_horizon import checks, before_migration, loaded_fixture


if __name__=='__main__':
    main(checks=checks,before_migration=before_migration,fixture_factory=loaded_fixture,through_migration='0271')
