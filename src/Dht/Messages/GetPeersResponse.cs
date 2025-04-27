//
// GetPeersResponse.cs
//
// Authors:
//   Alan McGovern <alan.mcgovern@gmail.com>
//
// Copyright (C) 2008 Alan McGovern
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
using System.Net; // Added for IPEndPoint
 
using MonoTorrent.BEncoding;

namespace MonoTorrent.Dht.Messages
{
    sealed class GetPeersResponse : ResponseMessage
    {
        static readonly BEncodedString NodesKey = new BEncodedString ("nodes");
        static readonly BEncodedString Nodes6Key = new BEncodedString ("nodes6"); // Added for IPv6
        static readonly BEncodedString TokenKey = new BEncodedString ("token");
        static readonly BEncodedString ValuesKey = new BEncodedString ("values");
        static readonly BEncodedString Values6Key = new BEncodedString ("values6"); // Added for IPv6
 
        /// <summary>
        /// The external endpoint of the sender, if known via NAT traversal.
        /// This isn't directly encoded but might be used by the handler creating this response.
        /// </summary>
        public IPEndPoint? ExternalEndPoint { get; }
 
        public BEncodedValue Token {
            get => Parameters.GetValueOrDefault (TokenKey) ?? throw new InvalidOperationException ("The required parameter 'token' was not set.");
            set => Parameters[TokenKey] = value;
        }

        public BEncodedString? Nodes {
            get => (BEncodedString?) Parameters.GetValueOrDefault (NodesKey);
            set {
                if (Parameters.ContainsKey (ValuesKey))
                    throw new InvalidOperationException ("Already contains the values key");
                if (value is null)
                    Parameters.Remove (NodesKey);
                else
                    Parameters[NodesKey] = value;
            }
        }
 
        // Optional IPv6 nodes
        public BEncodedString? Nodes6 {
            get => (BEncodedString?) Parameters.GetValueOrDefault (Nodes6Key);
            set {
                 if (Parameters.ContainsKey (ValuesKey) || Parameters.ContainsKey (Values6Key))
                    throw new InvalidOperationException ("Cannot contain Nodes6 and Values/Values6 keys");
                if (value is null)
                    Parameters.Remove (Nodes6Key);
                else
                    Parameters[Nodes6Key] = value;
            }
        }
 
        public BEncodedList? Values {
            get => (BEncodedList?) Parameters.GetValueOrDefault (ValuesKey);
            set {
                if (Parameters.ContainsKey (NodesKey) || Parameters.ContainsKey (Nodes6Key))
                    throw new InvalidOperationException ("Already contains the nodes/nodes6 key");
                if (value is null)
                    Parameters.Remove (ValuesKey);
                else
                    Parameters[ValuesKey] = value;
            }
        }
 
        // Optional IPv6 values
        public BEncodedList? Values6 {
            get => (BEncodedList?) Parameters.GetValueOrDefault (Values6Key);
            set {
                 if (Parameters.ContainsKey (NodesKey) || Parameters.ContainsKey (Nodes6Key))
                    throw new InvalidOperationException ("Already contains the nodes/nodes6 key");
                if (value is null)
                    Parameters.Remove (Values6Key);
                else
                    Parameters[Values6Key] = value;
            }
        }
 
        public GetPeersResponse (NodeId id, BEncodedValue transactionId, BEncodedString token, IPEndPoint? externalEndPoint = null)
            : base (id, transactionId)
        {
            ExternalEndPoint = externalEndPoint; // Store it
            Parameters.Add (TokenKey, token);
        }

        public GetPeersResponse (BEncodedDictionary d)
            : base (d)
        {

        }

        public override void Handle (DhtEngine engine, Node node)
        {
            base.Handle (engine, node);
            node.Token = Token;
        }
    }
}
