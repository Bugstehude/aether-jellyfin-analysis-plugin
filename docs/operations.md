# Operations, backup and rollback

## Data ownership

The plugin creates `aether-analysis.sqlite` only inside its Jellyfin-assigned plugin data folder.
It never modifies Jellyfin's library database. SQLite WAL and shared-memory side files can exist
while Jellyfin is running.

## Capacity

The default compressed-analysis ceiling is 10 GiB. Administrators can change it in the plugin
configuration. Scheduled and manual cleanup apply retention first and then least-recently-used
eviction. The admin status endpoint reports record count, compressed bytes, configured ceiling,
latest cleanup and non-sensitive operational counters.

## Consistent LXC/container backup

1. Stop Jellyfin cleanly so SQLite checkpoints its WAL.
2. Back up the complete Jellyfin configuration/data volume, including the plugin data folder. Do
   not copy only the main `.sqlite` file while the server is running.
3. Record the installed plugin version and Jellyfin version with the backup.
4. Start Jellyfin and verify `/System/Info/Public` plus the authenticated plugin status endpoint.

For multi-gigabyte stores, a filesystem or LXC snapshot taken after stopping Jellyfin is preferred
over file-by-file copying.

## Restore

1. Stop Jellyfin.
2. Restore plugin DLL and plugin data from the same backup generation.
3. Preserve file ownership and permissions used by the Jellyfin service account.
4. Start Jellyfin. Checked-in EF migrations run automatically and are forward-only.
5. Verify record count, stored bytes and a compact analysis read before reopening AETHER clients.

## Plugin rollback

Before upgrading, retain the previous plugin ZIP and take a consistent data backup. If startup or
smoke validation fails, stop Jellyfin, restore both the previous DLL and its matching plugin data,
then restart. Replacing only the DLL after a database migration is not a supported rollback.

## Uninstall

Stop Jellyfin, remove the plugin through Jellyfin's supported plugin-management path, and retain or
delete `aether-analysis.sqlite` according to the administrator's data-retention decision. Removing
the plugin must never remove source media or Jellyfin library metadata.
