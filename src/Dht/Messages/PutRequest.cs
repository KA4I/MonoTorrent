//
// PutRequest.cs
//
// Authors:
//   Alan McGovern alan.mcgovern@gmail.com
//   Roo (implementing BEP44)
//
// Copyright (C) 2008 Alan McGovern
// Copyright (C) 2025 Roo
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//


using MonoTorrent.BEncoding;

namespace MonoTorrent.Dht.Messages
{
    /// <summary>
    /// Represents a 'put' request in the DHT protocol (BEP44).
    /// Used for both immutable and mutable items.
    /// </summary>
    internal class PutRequest : QueryMessage
    {
        static readonly BEncodedString TokenKey = new BEncodedString ("token");
        static readonly BEncodedString ValueKey = new BEncodedString ("v");
        static readonly BEncodedString PublicKeyKey = new BEncodedString ("k");
        static readonly BEncodedString SaltKey = new BEncodedString ("salt");
        static readonly BEncodedString SeqKey = new BEncodedString ("seq");
        static readonly BEncodedString SignatureKey = new BEncodedString ("sig");
        static readonly BEncodedString CasKey = new BEncodedString ("cas");
        static readonly BEncodedString QueryName = new BEncodedString ("put");

        public BEncodedString Token => (BEncodedString) Parameters[TokenKey];
        public BEncodedValue Value => Parameters[ValueKey];

        // Mutable item specific fields
        public BEncodedString? PublicKey {
            get => Parameters.TryGetValue (PublicKeyKey, out BEncodedValue? val) ? (BEncodedString) val : null;
            private set {
                if (value == null)
                    Parameters.Remove (PublicKeyKey);
                else
                    Parameters[PublicKeyKey] = value;
            }
        }

        public BEncodedString? Salt {
            get => Parameters.TryGetValue (SaltKey, out BEncodedValue? val) ? (BEncodedString) val : null;
            private set {
                if (value == null)
                    Parameters.Remove (SaltKey);
                else
                    Parameters[SaltKey] = value;
            }
        }

        public long? SequenceNumber {
            get => Parameters.TryGetValue (SeqKey, out BEncodedValue? val) ? ((BEncodedNumber) val).Number : null;
            private set {
                if (value.HasValue)
                    Parameters[SeqKey] = new BEncodedNumber (value.Value);
                else
                    Parameters.Remove (SeqKey);
            }
        }

        public BEncodedString? Signature {
            get => Parameters.TryGetValue (SignatureKey, out BEncodedValue? val) ? (BEncodedString) val : null;
            private set {
                if (value == null)
                    Parameters.Remove (SignatureKey);
                else
                    Parameters[SignatureKey] = value;
            }
        }

        public long? Cas {
            get => Parameters.TryGetValue (CasKey, out BEncodedValue? val) ? ((BEncodedNumber) val).Number : null;
            private set {
                if (value.HasValue)
                    Parameters[CasKey] = new BEncodedNumber (value.Value);
                else
                    Parameters.Remove (CasKey);
            }
        }

        // Constructor for Immutable items
        public PutRequest (NodeId id, BEncodedString token, BEncodedValue value)
             : base (id, QueryName)
        {
            Parameters.Add (TokenKey, token);
            Parameters.Add (ValueKey, value);
        }

        // Constructor for Mutable items
        public PutRequest (NodeId id, BEncodedString token, BEncodedValue value, BEncodedString publicKey, BEncodedString? salt, long sequenceNumber, BEncodedString signature, long? cas = null)
            : base (id, QueryName)
        {
            Parameters.Add (TokenKey, token);
            Parameters.Add (ValueKey, value);
            PublicKey = publicKey; // Use property setter
            Salt = salt;           // Use property setter
            SequenceNumber = sequenceNumber; // Use property setter
            Signature = signature; // Use property setter
            Cas = cas;             // Use property setter
        }

        public PutRequest (BEncodedDictionary d)
            : base (d)
        {
        }

        public override ResponseMessage CreateResponse (BEncodedDictionary parameters)
        {
            // We need to create PutResponse.cs next
            return new PutResponse (parameters);
        }
    }
}