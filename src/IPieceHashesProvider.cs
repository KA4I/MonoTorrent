namespace MonoTorrent
{
    public interface IPieceHashesProvider
    {
        IPieceHashes PieceHashes { get; }
    }
}
