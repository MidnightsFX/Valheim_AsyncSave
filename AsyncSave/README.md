# AsyncSave

AsyncSave is a Valheim mod that offloads world and character saves from the main thread, eliminating stutters during saves by distributing work across frames.

## How It Works

### World Save (ZDO Serialization)
- Spreads world save serialization across multiple frames using `SnapshotCoroutine`, processing ZDOs incrementally per slice
- Configurable slice budget (default: 2ms per slice) to balance performance vs. total snapshot time
- Delta reconciliation re-serializes changed ZDOs during snapshot yields
- Handles game state mutations during long saves via atomic sector snapshots

### Character Save (Profile Serialization)
- Moves profile serialization to a background thread
- Maintains main thread operations (building ZPackage) while offloading compression and file write
- Preserves vanilla save format byte-for-byte with background I/O
- Handles map payload transfer between threads using `MapSnapshot`

> Note: This mod does not modify game content or behavior, strictly optimizing save timing.