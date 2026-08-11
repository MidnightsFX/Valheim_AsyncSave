# Changelog

## 0.3.0

Initial release.

- World saves walk `ZDOMan`'s sectors across frames instead of cloning every ZDO on one frame,
  serializing straight into a reusable buffer while the writer thread waits on it.
- Sector scratch copies, `AddToSector` tracking and revision reconciliation keep the result
  consistent even though the walk now spans frames.
- Buffer and dedup table are split into 64 MB segments, so a world larger than .NET's 2 GB
  array ceiling still saves.
- Character saves bulk-copy the explored map arrays and hand compression, hashing and the
  `.fch` write to a background worker. The byte sequence is identical to vanilla's.
- Adaptive slice budget scales each slice to the frame time the game is not already using, so
  a save costs little while the game is struggling.
- `/save` and the in-game menu Save button are both supported; quitting, logging out and
  shutdown always use the untouched vanilla path.
- `Dirty` reconciliation re-encodes only what the revision hooks flagged and costs nothing on
  an idle world; `FullScan` re-checks every ZDO.
- Automatic stand-downs: if a patch fails, the `.fch` layout is unrecognized, the revision
  hooks are missing, or the host is big-endian, the affected half disables itself for that
  session only and logs why. The config file is never rewritten.
