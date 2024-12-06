using System;
using System.IO;
using System.Linq;

using MonoTorrent.BEncoding;

namespace MonoTorrent;

public readonly struct TorrentPath (params string[] parts): IComparable<TorrentPath>, IEquatable<TorrentPath>
{
    readonly string[] _parts = AtLeastOne(parts).Select (ValidatePart).ToArray ();

    public TorrentPath (BEncodedList parts)
        : this (parts?.Select (v => ((BEncodedString) v).Text).ToArray ()
                ?? throw new ArgumentNullException (nameof(parts)))
    {
    }

    public ReadOnlySpan<string> Parts => _parts;
    public static TorrentPath Combine (params string[] parts) => new(parts);

    public override string ToString () => Path.Combine (_parts);
    public string ToString(char separator) => string.Join (separator.ToString(), _parts);

    public BEncodedList Encode () => new(_parts.Select (p => new BEncodedString (p)));

    public bool Equals (TorrentPath other) => _parts.SequenceEqual (other._parts);
    public static bool operator == (TorrentPath left, TorrentPath right) => left._parts.SequenceEqual (right._parts);
    public static bool operator != (TorrentPath left, TorrentPath right) => !left._parts.SequenceEqual (right._parts);

    public static TorrentPath operator / (TorrentPath parent, string child) => new(parent._parts.Append (child).ToArray ());

    public int CompareTo (TorrentPath other)
    {
        int length = Math.Min (_parts.Length, other._parts.Length);
        for (int i = 0; i < length; i++) {
            int cmp = string.Compare (_parts[i], other._parts[i], StringComparison.Ordinal);
            if (cmp != 0)
                return cmp;
        }

        return _parts.Length.CompareTo (other._parts.Length);
    }

    public static bool IsValidPart (string part)
    {
        if (part is null)
            throw new ArgumentNullException (nameof(part));

        if (part.Length == 0)
            return false;

        if (part.IndexOfAny (Path.GetInvalidFileNameChars ()) >= 0)
            return false;

        if (part.IndexOfAny ([':', '/', '\\']) >= 0)
            return false;

        if (part is "." or "..")
            return false;

        return true;
    }
    static string ValidatePart (string part) =>
        IsValidPart (part) ? part : throw new ArgumentException ("Invalid part", nameof(part));

    static T[] AtLeastOne<T> (T[] array) => array.Length > 0
        ? array
        : throw new ArgumentException ("At least one element is required", nameof(array));
}
