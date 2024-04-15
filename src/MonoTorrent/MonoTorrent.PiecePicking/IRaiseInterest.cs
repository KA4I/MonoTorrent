using System.Threading.Tasks;

namespace MonoTorrent.PiecePicking;

public interface IRaiseInterest
{
    Task RaiseInterest ();
}
