# JellyBridge-SQLite architecture

JellyBridge-SQLite is a clean SQLite-first fork of JellyBridge.

## Design goals

- Scale to 50,000+ Discover movies without using `metadata.json` as plugin state.
- Keep Jellyfin's own library, filter, sort, genre, year, rating, search and UI behavior.
- Keep Seerr/Jellyseerr request integration and user permission mapping.
- Keep ordinary Jellyfin libraries named `Discover Movies` and `Discover Series`.
- Do not add a custom Discover channel or a custom filter UI.
- Do not migrate legacy JellyBridge metadata/state. A new install starts with an empty local state database.
- Treat filesystem artifacts as output for Jellyfin, never as the source of truth.

## Data ownership

### Discover Catalog

The UI ARR Discover Catalog remains the master catalog.

It owns the large catalog database and exposes catalog selection through the existing SQLite-backed catalog API. JellyBridge-SQLite must not open or write the catalog database directly.

The catalog is responsible for:

- catalog membership
- ranking and selection
- TMDB identity
- title/overview/release date
- genres
- ratings
- poster/backdrop metadata
- country/language metadata

### JellyBridge-SQLite local state

JellyBridge-SQLite owns a small local `jellybridge.db`.

It stores only operational state required to calculate and apply incremental syncs:

- media type
- TMDB ID
- target path
- content fingerprint
- materialization state
- generation number
- last seen/materialized timestamps
- sync run state

It does not mirror the full Discover Catalog.

## Sync model

A sync is a set comparison:

```
desired set from Discover Catalog
            │
            ▼
      SyncPlanner
            │
       SQL set diff
   ┌────────┼────────┐
   ▼        ▼        ▼
  ADD     UPDATE   REMOVE
```

Unchanged items are not re-materialized.

For a 50,000 item library, if 40 items enter the working set and 40 leave it, the plugin should process approximately those changes rather than re-reading 50,000 metadata files.

## Jellyfin integration

JellyBridge-SQLite materializes normal Jellyfin library items with complete metadata.

Jellyfin itself handles:

- filters
- genre
- year
- rating
- sorting
- search
- library browsing

JellyBridge-SQLite must not recreate those features.

## Filesystem policy

NFO files and tiny placeholder media may be produced when Jellyfin requires them.

They are derived output.

The plugin must not scan NFO or JSON files to reconstruct global plugin state.

Local poster/backdrop/logo copies are not part of the core design. Jellyfin and its metadata/image providers should manage image caching wherever possible.

## Request flow

The existing favorite-to-Seerr request flow is retained.

The request integration remains separate from catalog selection and SQLite state.

## Initial scale gates

Development should be verified at progressively larger working sets:

1. 1,000 movies + 1,000 series
2. 5,000 movies + 5,000 series
3. 50,000 movies + the selected series target

At each gate measure:

- catalog query time
- SQLite diff time
- materialization time
- Jellyfin library scan time
- state DB size
- RAM usage
- Jellyfin UI/filter responsiveness
