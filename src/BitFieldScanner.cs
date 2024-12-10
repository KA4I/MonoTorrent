using System;
using System.Collections;
using System.Collections.Generic;

namespace MonoTorrent
{
    struct BitFieldDataScanner : IEnumerator<int>
    {
        readonly BitFieldData data;
        readonly bool lookingFor;
        int last;

        public BitFieldDataScanner (BitFieldData data, bool lookingFor)
        {
            this.data = data ?? throw new ArgumentNullException (nameof(data));
            this.lookingFor = lookingFor;
            this.last = -1;
        }

        public bool MoveNext ()
        {
            if (this.last + 1 >= this.data.Length)
                return false;

            int next = this.lookingFor == true
                ? this.data.FirstTrue (this.last + 1, this.data.Length - 1)
                : this.data.FirstFalse (this.last + 1, this.data.Length - 1);

            if (next < 0) {
                this.last = this.data.Length;
                return false;
            }
            this.last = next;
            return true;
        }

        public void Reset ()
        {
            this.last = -1;
        }

        public int Current
            => this.last >= 0 && this.last < this.data.Length
                ? this.last
                : throw new InvalidOperationException ($"Enumerator at {this.last}");

        object IEnumerator.Current => Current;

        public void Dispose ()
        {
        }
    }

    public struct BitFieldScanner : IEnumerator<int>
    {
        BitFieldDataScanner scanner;

        internal BitFieldScanner (BitFieldDataScanner scanner)
        {
            this.scanner = scanner;
        }

        public bool MoveNext () => scanner.MoveNext ();

        public void Reset () => scanner.Reset ();

        public int Current => scanner.Current;

        object IEnumerator.Current => ((IEnumerator) scanner).Current;

        public void Dispose () => scanner.Dispose ();
    }

    public struct BitFieldIndices : IEnumerable<int>
    {
        readonly BitFieldData data;
        readonly bool lookingFor;

        internal BitFieldIndices (BitFieldData data, bool lookingFor)
        {
            this.data = data ?? throw new ArgumentNullException (nameof(data));
            this.lookingFor = lookingFor;
        }

        public BitFieldScanner GetEnumerator ()
            => new BitFieldScanner (new BitFieldDataScanner (this.data, this.lookingFor));

        IEnumerator<int> IEnumerable<int>.GetEnumerator ()
            => GetEnumerator ();

        IEnumerator IEnumerable.GetEnumerator ()
            => GetEnumerator ();
    }
}
