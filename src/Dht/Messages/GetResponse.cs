//
// GetResponse.cs
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


using System;
using MonoTorrent.BEncoding;

namespace MonoTorrent.Dht.Messages
{
    /// <summary>
    /// Represents a response to a 'get' request in the DHT protocol (BEP44).
    /// Contains the stored value and potentially other information like nodes, token, signature etc.
    /// </summary>
    internal class GetResponse : ResponseMessage
    {
        static readonly BEncodedString ValueKey = new BEncodedString ("v");
        static readonly BEncodedString TokenKey = new BEncodedString ("token");
        static readonly BEncodedString NodesKey = new BEncodedString ("nodes");
        static readonly BEncodedString Nodes6Key = new BEncodedString ("nodes6");
        static readonly BEncodedString PublicKeyKey = new BEncodedString ("k");
        static readonly BEncodedString SeqKey = new BEncodedString ("seq");
        static readonly BEncodedString SignatureKey = new BEncodedString ("sig");

        public BEncodedValue? Value {
            get => Parameters.TryGetValue (ValueKey, out BEncodedValue? val) ? val : null;
            set {
                if (value == null)
                    Parameters.Remove (ValueKey);
                else
                    Parameters[ValueKey] = value;
            }
        }

        public BEncodedString? Token {
            get => Parameters.TryGetValue (TokenKey, out BEncodedValue? val) ? (BEncodedString) val : null;
            set {
                if (value == null)
                    Parameters.Remove (TokenKey);
                else
                    Parameters[TokenKey] = value;
            }
        }

        public BEncodedString? Nodes {
            get => Parameters.TryGetValue (NodesKey, out BEncodedValue? val) ? (BEncodedString) val : null;
            set {
                if (value == null)
                    Parameters.Remove (NodesKey);
                else
                    Parameters[NodesKey] = value;
            }
        }

        public BEncodedString? Nodes6 {
            get => Parameters.TryGetValue (Nodes6Key, out BEncodedValue? val) ? (BEncodedString) val : null;
            set {
                if (value == null)
                    Parameters.Remove (Nodes6Key);
                else
                    Parameters[Nodes6Key] = value;
            }
        }

        // Mutable item specific fields
        public BEncodedString? PublicKey {
            get => Parameters.TryGetValue (PublicKeyKey, out BEncodedValue? val) ? (BEncodedString) val : null;
            set {
                if (value == null)
                    Parameters.Remove (PublicKeyKey);
                else
                    Parameters[PublicKeyKey] = value;
            }
        }

        public long? SequenceNumber {
            get => Parameters.TryGetValue (SeqKey, out BEncodedValue? val) ? ((BEncodedNumber) val).Number : null;
            set {
                if (value.HasValue)
                    Parameters[SeqKey] = new BEncodedNumber (value.Value);
                else
                    Parameters.Remove (SeqKey);
            }
        }

        public BEncodedString? Signature {
            get => Parameters.TryGetValue (SignatureKey, out BEncodedValue? val) ? (BEncodedString) val : null;
            set {
                if (value == null)
                    Parameters.Remove (SignatureKey);
                else
                    Parameters[SignatureKey] = value;
            }
        }

        public GetResponse (NodeId id, BEncodedValue transactionId)
            : base (id, transactionId)
        {
        }

        public GetResponse (BEncodedDictionary d)
            : base (d)
        {
        }
    }
}