namespace MonoTorrent.PieceWriter
{
    public interface IPieceHashStatusUpdater
    {
        /// <summary>
        /// Updates the hash status of a piece
        /// </summary>
        /// <param name="index">Piece index</param>
        /// <param name="hashPassed"><c>true</c> if our local copy of the piece has the expected hash.</param>
        /// <param name="hashed">How many pieces we updated hash status for so far, including this one.
        /// <para>Set to <c>0</c> to indicate that this piece hash status is unknown.
        /// In this case, the value in <paramref name="hashPassed"/> will be ignored.
        /// </para>
        /// </param>
        /// <param name="totalHashing">How many pieces we are planning to hash in total</param>
        void UpdatePieceHashStatus(int index, bool hashPassed, int hashed, int totalHashing);
    }
}
