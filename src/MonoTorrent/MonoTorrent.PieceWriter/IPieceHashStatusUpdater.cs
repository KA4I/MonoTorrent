namespace MonoTorrent.PieceWriter
{
    public interface IPieceHashStatusUpdater
    {
        void UpdatePieceHashStatus(int index, bool hashPassed, int hashed, int totalHashing);
    }
}
