# Library Artwork

Jellyfin builds a library tile from the items inside it without knowing who is looking, so
a Kids profile can end up with a Movies tile showing a film it cannot open.

Give a profile its own picture for a library, or no artwork at all, under **Edit profile →
Library Artwork**. With no artwork the tile falls back to its icon and name; libraries left
alone keep Jellyfin's own.

Two limits: the swap happens in the browser, so the original is still downloaded, and it
only applies where Bonfire runs.
