namespace AsyncSave.Snapshot
{
    /// One serialized ZDO in the snapshot buffer. Struct for zero heap overhead;
    /// stored in a pooled List.
    internal struct SnapshotEntry
    {
        public ZDOID Uid;
        public int Offset;
        public int Length;
        public uint DataRevision;
    }
}
