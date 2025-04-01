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

    public override bool Equals(object? obj) => obj is TorrentPath other && Equals(other);

    public override int GetHashCode()
    {
        int hashCode = 0;
        foreach (var part in _parts)
            // Simple hash combination - consider a better one if collisions become an issue.
            hashCode = (hashCode * 397) ^ part.GetHashCode();
        return hashCode;
    }


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

    static readonly char[] InvalidPartChars = Path.GetInvalidFileNameChars ()
        .Concat ([':', '/', '\\']).Distinct ().ToArray ();
    public static bool IsValidPart (string part)
    {
        if (part is null)
            throw new ArgumentNullException (nameof(part));

        if (part.Length == 0)
            return false;

        if (part is "." or "..")
            return false;

        if (part.IndexOfAny (InvalidPartChars) >= 0)
            return false;

        return true;
    }
    
    static string ValidatePart (string part)
    {
        if (part is null)
            throw new ArgumentNullException (nameof(part));

        if (part.Length == 0)
            throw new ArgumentException ("Part cannot be empty", nameof(part));

        // Check for path busting attempts - these must throw exceptions
        if (part is "." or "..")
            throw new ArgumentException ("Part cannot be '.' or '..'", nameof(part));

        // Check for relative path elements.
        if (part.Contains("..") || part.Contains("/.") || part.Contains("\\."))
            throw new ArgumentException ("Part cannot contain relative path elements", nameof(part));

        // Explicitly check for Windows drive letter format like "C:" before other root checks.
        if (part.Length == 2 && char.IsLetter(part[0]) && part[1] == ':') {
            throw new ArgumentException("Part cannot be a drive letter.", nameof(part));
        }

        // Check if the original part contained path separators, which are disallowed *within* a part.
        if (part.IndexOf(Path.DirectorySeparatorChar) >= 0 || part.IndexOf(Path.AltDirectorySeparatorChar) >= 0)
            throw new ArgumentException ("Part cannot contain directory separators.", nameof(part));

        // Check for rooted paths *before* sanitizing.
        try {
            if (Path.IsPathRooted(part)) {
                 throw new ArgumentException ("Part cannot be a rooted path", nameof(part));
            }
            // If IsPathRooted returns false or doesn't throw, it's not rooted. Proceed.
        } catch (ArgumentException) {
            // If IsPathRooted throws (NetFx with invalid chars), it means it's not rooted in the traditional sense *and* contains invalid chars.
            // We should proceed to sanitize these characters, not throw here.
            // So, catching the exception here means we continue to sanitization.
        }

        // Sanitize invalid file name characters (excluding separators, already checked).
        string sanitized = part;
        var invalidFileNameCharsOnly = Path.GetInvalidFileNameChars();
        foreach (char c in invalidFileNameCharsOnly)
        {
            // Use Replace(string, string) for broader compatibility. Avoid replacing separators if they somehow end up in InvalidFileNameChars.
            if (c != Path.DirectorySeparatorChar && c != Path.AltDirectorySeparatorChar && sanitized.IndexOf(c) != -1)
                sanitized = sanitized.Replace(c.ToString(), "_");
        }

        // The part should now be sanitized and validated against rooting and separators.
        return sanitized;
    }

    static T[] AtLeastOne<T> (T[] array) => array.Length > 0
        ? array
        : throw new ArgumentException ("At least one element is required", nameof(array));
}
